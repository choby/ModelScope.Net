using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelScope.Net.AspNetCore;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.AspNetCore.Tests;

public sealed class ModelScopeTelemetryTests
{
    private const string Secret = "TOP-SECRET-PROMPT-AND-TOKEN";

    [Fact]
    public async Task InstrumentedSession_EmitsTracesMetricsAndAuditWithoutSensitiveContent()
    {
        var auditSink = new CapturingAuditSink();
        var activities = new ConcurrentBag<string>();
        var measurements = new ConcurrentBag<string>();
        using var activityListener = CreateActivityListener(activities);
        using var meterListener = CreateMeterListener(measurements);
        await using var provider = CreateProvider(auditSink, fail: false);
        var router = provider.GetRequiredService<RuntimeRouter>();
        var capabilities = CreateCapabilities();
        await using var session = await router.CreateSessionAsync(capabilities, preferredRuntime: "test-runtime");
        var request = new ModelRequest(
            "chat",
            JsonSerializer.SerializeToElement(new { prompt = Secret }),
            Parameters: new Dictionary<string, string> { ["authorization"] = Secret });

        var response = await session.InvokeAsync(request);
        var stream = new List<ModelStreamEvent>();
        await foreach (var streamEvent in session.InvokeStreamingAsync(request))
        {
            stream.Add(streamEvent);
        }

        Assert.Contains(Secret, response.Output.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(stream, item => item.Data.GetRawText().Contains(Secret, StringComparison.Ordinal));
        Assert.Equal(2, auditSink.Events.Count);
        Assert.All(auditSink.Events, auditEvent => Assert.Equal("succeeded", auditEvent.Outcome));
        Assert.Contains(activities, value => value.Contains("modelscope.runtime.invoke", StringComparison.Ordinal));
        Assert.Contains(activities, value => value.Contains("modelscope.runtime.invoke_stream", StringComparison.Ordinal));
        Assert.Contains(measurements, value => value.StartsWith("modelscope.runtime.invocations=", StringComparison.Ordinal));
        Assert.Contains(measurements, value => value.StartsWith("modelscope.runtime.duration=", StringComparison.Ordinal));

        var observableEvidence = string.Join('\n', activities.Concat(measurements)) +
            JsonSerializer.Serialize(auditSink.Events);
        Assert.DoesNotContain(Secret, observableEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(capabilities.ModelId!, observableEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(capabilities.ModelPath, observableEvidence, StringComparison.Ordinal);
        Assert.All(auditSink.Events, auditEvent => Assert.Matches("^[0-9a-f]{16}$", auditEvent.ModelFingerprint));
    }

    [Fact]
    public async Task InstrumentedSession_FailureRecordsOnlyStableErrorCode()
    {
        var auditSink = new CapturingAuditSink();
        var activities = new ConcurrentBag<string>();
        using var activityListener = CreateActivityListener(activities);
        await using var provider = CreateProvider(auditSink, fail: true);
        var router = provider.GetRequiredService<RuntimeRouter>();
        await using var session = await router.CreateSessionAsync(CreateCapabilities(), preferredRuntime: "test-runtime");
        var request = new ModelRequest("chat", JsonSerializer.SerializeToElement(new { prompt = Secret }));

        var exception = await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(request));

        Assert.Contains(Secret, exception.Message, StringComparison.Ordinal);
        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal("failed", auditEvent.Outcome);
        Assert.Equal(nameof(ModelScopeErrorCode.InferenceFailed).Replace("ModelScopeErrorCode.", string.Empty), auditEvent.ErrorCode);
        var observableEvidence = string.Join('\n', activities) + JsonSerializer.Serialize(auditSink.Events);
        Assert.DoesNotContain(Secret, observableEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstrumentedSession_CanDisableAllSignals()
    {
        var auditSink = new CapturingAuditSink();
        var telemetry = new ModelScopeTelemetry(auditSink, new ModelScopeTelemetryOptions
        {
            EnableAudit = false,
            EnableMetrics = false,
            EnableTracing = false,
        });
        var runtime = new TestRuntime(fail: false);
        var router = new RuntimeRouter([runtime], instrumentation: telemetry);
        await using var session = await router.CreateSessionAsync(CreateCapabilities(), preferredRuntime: runtime.Name);

        await session.InvokeAsync(new ModelRequest("chat", JsonSerializer.SerializeToElement(new { prompt = Secret })));

        Assert.Empty(auditSink.Events);
    }

    [Fact]
    public async Task InstrumentedStream_EarlyDisposalIsAuditedAsAbandoned()
    {
        var auditSink = new CapturingAuditSink();
        var telemetry = new ModelScopeTelemetry(auditSink);
        var router = new RuntimeRouter([new TestRuntime(fail: false)], instrumentation: telemetry);
        await using var session = await router.CreateSessionAsync(CreateCapabilities(), preferredRuntime: "test-runtime");
        var request = new ModelRequest("chat", JsonSerializer.SerializeToElement(new { prompt = Secret }));
        await using (var enumerator = session.InvokeStreamingAsync(request).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.False(enumerator.Current.IsTerminal);
        }

        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal("abandoned", auditEvent.Outcome);
        Assert.True(auditEvent.Streaming);
    }

    [Fact]
    public async Task AuditSinkFailure_IsFailOpenAndEmitsFailureMetric()
    {
        var measurements = new ConcurrentBag<string>();
        using var meterListener = CreateMeterListener(measurements);
        var telemetry = new ModelScopeTelemetry(new ThrowingAuditSink());
        var router = new RuntimeRouter([new TestRuntime(fail: false)], instrumentation: telemetry);
        await using var session = await router.CreateSessionAsync(CreateCapabilities(), preferredRuntime: "test-runtime");

        var response = await session.InvokeAsync(
            new ModelRequest("chat", JsonSerializer.SerializeToElement(new { prompt = Secret })));

        Assert.Contains(Secret, response.Output.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(measurements, value =>
            value.StartsWith("modelscope.audit.write_failures=1", StringComparison.Ordinal));
    }

    private static ServiceProvider CreateProvider(CapturingAuditSink auditSink, bool fail)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModelScopeAuditSink>(auditSink);
        services.AddSingleton<IModelRuntime>(new TestRuntime(fail));
        services.AddModelScopeNet();
        return services.BuildServiceProvider();
    }

    private static ActivityListener CreateActivityListener(ConcurrentBag<string> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ModelScopeTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(
                activity.OperationName + ";" +
                string.Join(';', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener CreateMeterListener(ConcurrentBag<string> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ModelScopeTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(FormatMeasurement(instrument, measurement, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Add(FormatMeasurement(instrument, measurement, tags)));
        listener.Start();
        return listener;
    }

    private static string FormatMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var values = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            values.Add($"{tag.Key}={tag.Value}");
        }

        return $"{instrument.Name}={measurement};{string.Join(';', values)}";
    }

    private static ModelCapabilities CreateCapabilities() => new(
        $"/private/{Secret}/model",
        $"tenant/{Secret}/model",
        "0123456789abcdef0123456789abcdef01234567",
        "chat",
        [],
        [],
        [],
        ContainsRemoteCode: false,
        []);

    private sealed class CapturingAuditSink : IModelScopeAuditSink
    {
        public ConcurrentBag<ModelScopeAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(ModelScopeAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuditSink : IModelScopeAuditSink
    {
        public ValueTask WriteAsync(ModelScopeAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"Sink failure includes {Secret}.");
    }

    private sealed class TestRuntime(bool fail) : IModelRuntime
    {
        public string Name => "test-runtime";

        public RuntimeCandidate Evaluate(ModelCapabilities capabilities) =>
            new(Name, CapabilityStatus.Certified, "test", 1000);

        public Task<IModelSession> CreateSessionAsync(
            ModelCapabilities capabilities,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IModelSession>(new TestSession(capabilities, fail));
    }

    private sealed class TestSession(ModelCapabilities capabilities, bool fail) : IModelSession
    {
        public ModelCapabilities Capabilities => capabilities;

        public Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            if (fail)
            {
                throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, $"Failure includes {Secret}.");
            }

            return Task.FromResult(new ModelResponse(
                JsonSerializer.SerializeToElement(new { output = Secret }),
                "test",
                "revision",
                "test-runtime",
                TimeSpan.Zero));
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelStreamEvent(
                "delta",
                JsonSerializer.SerializeToElement(new { output = Secret }),
                IsTerminal: false);
            yield return new ModelStreamEvent(
                "completed",
                JsonSerializer.SerializeToElement(new { output = Secret }),
                IsTerminal: true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
