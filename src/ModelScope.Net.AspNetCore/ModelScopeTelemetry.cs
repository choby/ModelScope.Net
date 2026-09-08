using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.AspNetCore;

public sealed class ModelScopeTelemetryOptions
{
    public bool EnableTracing { get; set; } = true;

    public bool EnableMetrics { get; set; } = true;

    public bool EnableAudit { get; set; } = true;

    public bool IncludeModelFingerprint { get; set; } = true;
}

public sealed record ModelScopeAuditEvent(
    DateTimeOffset Timestamp,
    string Operation,
    string Outcome,
    string Runtime,
    string Task,
    string ModelFingerprint,
    bool Streaming,
    double DurationMilliseconds,
    string? ErrorCode,
    string? TraceId);

public interface IModelScopeAuditSink
{
    ValueTask WriteAsync(ModelScopeAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class LoggingModelScopeAuditSink(ILogger<LoggingModelScopeAuditSink> logger) : IModelScopeAuditSink
{
    public ValueTask WriteAsync(ModelScopeAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "ModelScope audit {Operation} {Outcome} Runtime={Runtime} Task={Task} Model={ModelFingerprint} " +
            "Streaming={Streaming} DurationMs={DurationMilliseconds} ErrorCode={ErrorCode} TraceId={TraceId}",
            auditEvent.Operation,
            auditEvent.Outcome,
            auditEvent.Runtime,
            auditEvent.Task,
            auditEvent.ModelFingerprint,
            auditEvent.Streaming,
            auditEvent.DurationMilliseconds,
            auditEvent.ErrorCode,
            auditEvent.TraceId);
        return ValueTask.CompletedTask;
    }
}

public sealed class ModelScopeTelemetry : IModelSessionInstrumentation
{
    public const string ActivitySourceName = "ModelScope.Net.Runtime";
    public const string MeterName = "ModelScope.Net.Runtime";
    public const string InvocationMetricName = "modelscope.runtime.invocations";
    public const string FailureMetricName = "modelscope.runtime.failures";
    public const string AuditWriteFailureMetricName = "modelscope.audit.write_failures";
    public const string DurationMetricName = "modelscope.runtime.duration";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> InvocationCounter = Meter.CreateCounter<long>(
        InvocationMetricName,
        description: "Number of model inference invocations.");
    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>(
        FailureMetricName,
        description: "Number of failed model inference invocations.");
    private static readonly Counter<long> AuditWriteFailureCounter = Meter.CreateCounter<long>(
        AuditWriteFailureMetricName,
        description: "Number of audit events that the configured sink failed to persist.");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>(
        DurationMetricName,
        unit: "ms",
        description: "End-to-end model inference duration.");

    private readonly IModelScopeAuditSink _auditSink;
    private readonly ModelScopeTelemetryOptions _options;

    public ModelScopeTelemetry(IModelScopeAuditSink auditSink, ModelScopeTelemetryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(auditSink);
        _auditSink = auditSink;
        _options = options ?? new ModelScopeTelemetryOptions();
    }

    public IModelSession Instrument(
        IModelSession session,
        ModelCapabilities capabilities,
        string runtimeName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeName);
        return new InstrumentedModelSession(
            session,
            this,
            NormalizeRuntime(runtimeName),
            NormalizeTask(capabilities.Task),
            CreateModelFingerprint(capabilities));
    }

    private static string NormalizeRuntime(string runtimeName)
    {
        var normalized = runtimeName.Trim().ToLowerInvariant();
        return normalized is "remote" or "python" or "onnx" or "onnx-embedding" or "onnx-text-generation" or
            "onnx-text-classification" or "onnx-image-classification" or "onnx-object-detection" or "gguf"
            ? normalized
            : "other";
    }

    private static string NormalizeTask(string? task)
    {
        if (string.IsNullOrWhiteSpace(task)) return "unknown";
        var normalized = task.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return normalized is "sentence-embedding" or "feature-extraction" or "embedding" or
            "text-classification" or "sentiment-analysis" or "image-classification" or
            "object-detection" or "image-object-detection" or "domain-specific-object-detection" or
            "text-generation" or "chat" or "chat-completion"
            ? normalized
            : "other";
    }

    private string CreateModelFingerprint(ModelCapabilities capabilities)
    {
        if (!_options.IncludeModelFingerprint || string.IsNullOrWhiteSpace(capabilities.ModelId))
        {
            return "not-recorded";
        }

        var value = $"{capabilities.ModelId.Trim()}\n{capabilities.Revision?.Trim()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    private Activity? StartActivity(string operation, string runtime, string task, string fingerprint, bool streaming)
    {
        if (!_options.EnableTracing) return null;
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("modelscope.runtime", runtime);
        activity?.SetTag("modelscope.task", task);
        activity?.SetTag("modelscope.model_fingerprint", fingerprint);
        activity?.SetTag("modelscope.streaming", streaming);
        return activity;
    }

    private async ValueTask CompleteAsync(
        Activity? activity,
        string operation,
        string outcome,
        string runtime,
        string task,
        string fingerprint,
        bool streaming,
        TimeSpan duration,
        Exception? exception)
    {
        var errorCode = SafeErrorCode(exception);
        activity?.SetTag("modelscope.outcome", outcome);
        if (errorCode is not null) activity?.SetTag("error.type", errorCode);
        activity?.SetStatus(outcome switch
        {
            "succeeded" => ActivityStatusCode.Ok,
            "failed" => ActivityStatusCode.Error,
            _ => ActivityStatusCode.Unset,
        });

        if (_options.EnableMetrics)
        {
            var tags = new TagList
            {
                { "modelscope.runtime", runtime },
                { "modelscope.task", task },
                { "modelscope.streaming", streaming },
                { "modelscope.outcome", outcome },
            };
            InvocationCounter.Add(1, tags);
            DurationHistogram.Record(duration.TotalMilliseconds, tags);
            if (outcome == "failed") FailureCounter.Add(1, tags);
        }

        if (_options.EnableAudit)
        {
            try
            {
                await _auditSink.WriteAsync(new ModelScopeAuditEvent(
                    DateTimeOffset.UtcNow,
                    operation,
                    outcome,
                    runtime,
                    task,
                    fingerprint,
                    streaming,
                    Math.Round(duration.TotalMilliseconds, 3),
                    errorCode,
                    activity?.TraceId.ToString()), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (_options.EnableMetrics)
                {
                    AuditWriteFailureCounter.Add(1,
                        new KeyValuePair<string, object?>("modelscope.runtime", runtime));
                }

                activity?.AddEvent(new ActivityEvent("modelscope.audit.write_failed"));
                // Observability must never change inference availability or expose request content.
            }
        }

        activity?.Dispose();
    }

    private static string? SafeErrorCode(Exception? exception) => exception switch
    {
        null => null,
        ModelScopeException modelScope => modelScope.Code.ToString(),
        OperationCanceledException => "Cancelled",
        _ => "Unhandled",
    };

    private sealed class InstrumentedModelSession(
        IModelSession inner,
        ModelScopeTelemetry owner,
        string runtime,
        string task,
        string fingerprint) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();
            var activity = owner.StartActivity(
                "modelscope.runtime.invoke",
                runtime,
                task,
                fingerprint,
                streaming: false);
            try
            {
                var response = await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                await owner.CompleteAsync(
                    activity,
                    "inference",
                    "succeeded",
                    runtime,
                    task,
                    fingerprint,
                    streaming: false,
                    stopwatch.Elapsed,
                    exception: null).ConfigureAwait(false);
                return response;
            }
            catch (Exception exception)
            {
                await owner.CompleteAsync(
                    activity,
                    "inference",
                    exception is OperationCanceledException ? "cancelled" : "failed",
                    runtime,
                    task,
                    fingerprint,
                    streaming: false,
                    stopwatch.Elapsed,
                    exception).ConfigureAwait(false);
                throw;
            }
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();
            var activity = owner.StartActivity(
                "modelscope.runtime.invoke_stream",
                runtime,
                task,
                fingerprint,
                streaming: true);
            Exception? failure = null;
            var terminalObserved = false;
            var enumerator = inner.InvokeStreamingAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ModelStreamEvent streamEvent;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                        streamEvent = enumerator.Current;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        throw;
                    }

                    terminalObserved |= streamEvent.IsTerminal;
                    yield return streamEvent;
                }
            }
            finally
            {
                var hadFailureBeforeDispose = failure is not null;
                Exception? disposeFailure = null;
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                    failure ??= exception;
                }

                var outcome = failure is OperationCanceledException
                    ? "cancelled"
                    : failure is not null
                        ? "failed"
                        : terminalObserved
                            ? "succeeded"
                            : "abandoned";
                await owner.CompleteAsync(
                    activity,
                    "inference",
                    outcome,
                    runtime,
                    task,
                    fingerprint,
                    streaming: true,
                    stopwatch.Elapsed,
                    failure).ConfigureAwait(false);
                if (!hadFailureBeforeDispose && disposeFailure is not null) throw disposeFailure;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
