using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxRuntimeAdapterTests
{
    private const string AddModelBase64 =
        "CAoSFE1vZGVsU2NvcGUuTmV0LlRlc3RzOlgKEAoBeAoBeRIDc3VtIgNBZGQSD2FkZC10d28tdmVjdG9yc1oPCgF4EgoKCAgBEgQKAggCWg8KAXkSCgoICAESBAoCCAJiEQoDc3VtEgoKCAgBEgQKAggCQgQKABAN";

    [Fact]
    public async Task CpuSession_RunsFloatTensorsAndReturnsShapeMetadata()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var runtime = new OnnxRuntimeAdapter();
            var capabilities = CreateCapabilities(directory);
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(capabilities).Status);

            await using var session = await runtime.CreateSessionAsync(capabilities);
            using var payload = JsonDocument.Parse("""
                { "inputs": { "x": [1.25, 2.5], "y": [2.75, 3.5] } }
                """);
            var response = await session.InvokeAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone()));

            var output = response.Output.GetProperty("outputs").GetProperty("sum");
            Assert.Equal([2], output.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray());
            Assert.Equal([4f, 6f], output.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray());
            Assert.Equal("Single", output.GetProperty("type").GetString());
            Assert.Equal("onnx", response.Runtime);

            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone(), Stream: true)))
            {
                events.Add(item);
            }

            Assert.Single(events);
            Assert.True(events[0].IsTerminal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CpuSession_RejectsMissingInputAndWrongShape()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            await using var session = await new OnnxRuntimeAdapter().CreateSessionAsync(CreateCapabilities(directory));
            using var missing = JsonDocument.Parse("""{ "x": [1, 2] }""");
            var missingException = await Assert.ThrowsAsync<ModelScopeException>(
                () => session.InvokeAsync(new ModelRequest("raw-onnx", missing.RootElement.Clone())));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, missingException.Code);

            using var wrongShape = JsonDocument.Parse("""{ "x": [1, 2, 3], "y": [4, 5, 6] }""");
            var shapeException = await Assert.ThrowsAsync<ModelScopeException>(
                () => session.InvokeAsync(new ModelRequest("raw-onnx", wrongShape.RootElement.Clone())));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, shapeException.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_MapsInvalidModelToStructuredLoadError()
    {
        var directory = CreateModelDirectory(validModel: false);
        try
        {
            var exception = await Assert.ThrowsAsync<ModelScopeException>(
                () => new OnnxRuntimeAdapter().CreateSessionAsync(CreateCapabilities(directory)));
            Assert.Equal(ModelScopeErrorCode.ModelLoadFailed, exception.Code);
            Assert.True(exception.IsRetryable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RequestedProviderUnavailable_IsDiagnosableAndFailsClosed()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var environment = new TestOnnxExecutionProviderEnvironment("CPUExecutionProvider");
            var runtime = new OnnxRuntimeAdapter(
                new OnnxRuntimeOptions { ExecutionProvider = OnnxExecutionProvider.Cuda },
                environment);

            var candidate = runtime.Evaluate(CreateCapabilities(directory));
            Assert.Equal(CapabilityStatus.Unavailable, candidate.Status);
            Assert.Contains("CUDAExecutionProvider", candidate.Reason, StringComparison.Ordinal);

            var error = Assert.Throws<ModelScopeException>(() => runtime.GetExecutionProviderSelection());
            Assert.Equal(ModelScopeErrorCode.RuntimeNotInstalled, error.Code);
            Assert.Contains("AllowCpuFallback=true", error.Message, StringComparison.Ordinal);

            var sessionError = await Assert.ThrowsAsync<ModelScopeException>(
                () => runtime.CreateSessionAsync(CreateCapabilities(directory)));
            Assert.Equal(ModelScopeErrorCode.RuntimeNotInstalled, sessionError.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitCpuFallback_IsVisibleAndCannotInheritCpuCertification()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var environment = new TestOnnxExecutionProviderEnvironment("CPUExecutionProvider");
            var options = new OnnxRuntimeOptions
            {
                ExecutionProvider = OnnxExecutionProvider.Cuda,
                DeviceId = 3,
                AllowCpuFallback = true,
            };
            options.CertifiedModels.Add("tests/add-vectors");
            var runtime = new OnnxRuntimeAdapter(options, environment);

            var selection = runtime.GetExecutionProviderSelection();
            Assert.Equal(OnnxExecutionProvider.Cuda, selection.Requested);
            Assert.Equal(OnnxExecutionProvider.Cpu, selection.Effective);
            Assert.Equal(0, selection.DeviceId);
            Assert.True(selection.UsedCpuFallback);
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(CreateCapabilities(directory)).Status);

            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            using var payload = JsonDocument.Parse("""{ "x": [1, 2], "y": [3, 4] }""");
            var response = await session.InvokeAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone()));

            Assert.Equal(OnnxExecutionProvider.Cpu, environment.ConfiguredProvider);
            Assert.Equal(0, environment.ConfiguredDeviceId);
            var warning = Assert.Single(response.Warnings!);
            Assert.Contains("CUDAExecutionProvider", warning, StringComparison.Ordinal);
            Assert.Contains("CPUExecutionProvider", warning, StringComparison.Ordinal);

            var streamEvent = Assert.Single(await ReadStreamAsync(
                session.InvokeStreamingAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone(), Stream: true))));
            Assert.Single(streamEvent.Warnings!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderSelection_PreservesRequestedDeviceAndConfiguration()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var environment = new TestOnnxExecutionProviderEnvironment(
                "CUDAExecutionProvider",
                "CPUExecutionProvider",
                "CUDAExecutionProvider");
            var runtime = new OnnxRuntimeAdapter(
                new OnnxRuntimeOptions
                {
                    ExecutionProvider = OnnxExecutionProvider.Cuda,
                    DeviceId = 2,
                    GpuMemoryLimitBytes = 1024,
                },
                environment);

            var selection = runtime.GetExecutionProviderSelection();

            Assert.Equal(OnnxExecutionProvider.Cuda, selection.Effective);
            Assert.Equal(2, selection.DeviceId);
            Assert.Equal(["CPUExecutionProvider", "CUDAExecutionProvider"], selection.AvailableProviders);
            var candidate = runtime.Evaluate(CreateCapabilities(directory));
            Assert.Equal("Cuda", candidate.CertificationProperties!["onnx.effectiveExecutionProvider"]);
            Assert.Equal("2", candidate.CertificationProperties["onnx.requestedDeviceId"]);
            Assert.Equal("2", candidate.CertificationProperties["onnx.effectiveDeviceId"]);
            Assert.Equal("test-version", candidate.CertificationProperties["onnx.runtimeVersion"]);
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            Assert.Equal(OnnxExecutionProvider.Cuda, environment.ConfiguredProvider);
            Assert.Equal(2, environment.ConfiguredDeviceId);
            Assert.Equal(1024, environment.ConfiguredGpuMemoryLimitBytes);
            Assert.False(environment.ConfiguredAllowCpuFallback);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OptionsAreFrozenAtConstruction()
    {
        var options = new OnnxRuntimeOptions();
        var runtime = new OnnxRuntimeAdapter(options, new TestOnnxExecutionProviderEnvironment("CPUExecutionProvider"));

        options.ExecutionProvider = OnnxExecutionProvider.Cuda;
        options.DeviceId = -1;
        options.GpuMemoryLimitBytes = -1;

        var selection = runtime.GetExecutionProviderSelection();
        Assert.Equal(OnnxExecutionProvider.Cpu, selection.Requested);
        Assert.Equal(0, selection.DeviceId);
    }

    [Fact]
    public async Task ProviderInitializationFailure_MapsToRuntimeNotInstalled()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var environment = new TestOnnxExecutionProviderEnvironment(
                "CUDAExecutionProvider",
                "CPUExecutionProvider")
            {
                ConfigureException = new DllNotFoundException("provider initialization failed"),
            };
            var runtime = new OnnxRuntimeAdapter(
                new OnnxRuntimeOptions
                {
                    ExecutionProvider = OnnxExecutionProvider.Cuda,
                    AllowCpuFallback = true,
                },
                environment);

            var error = await Assert.ThrowsAsync<ModelScopeException>(
                () => runtime.CreateSessionAsync(CreateCapabilities(directory)));

            Assert.Equal(ModelScopeErrorCode.RuntimeNotInstalled, error.Code);
            Assert.Contains("CUDAExecutionProvider", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, environment.ConfigureCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(OnnxExecutionProvider.Cpu, 1, null)]
    [InlineData(OnnxExecutionProvider.CoreML, 1, null)]
    [InlineData(OnnxExecutionProvider.DirectML, 0, 1024L)]
    public void InvalidProviderOptions_AreRejectedAtConstruction(
        OnnxExecutionProvider provider,
        int deviceId,
        long? gpuMemoryLimitBytes)
    {
        Assert.ThrowsAny<ArgumentException>(() => new OnnxRuntimeAdapter(
            new OnnxRuntimeOptions
            {
                ExecutionProvider = provider,
                DeviceId = deviceId,
                GpuMemoryLimitBytes = gpuMemoryLimitBytes,
            },
            new TestOnnxExecutionProviderEnvironment("CPUExecutionProvider")));
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/add-vectors",
        "fixture-v1",
        "raw-onnx",
        [],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(bool validModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-onnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "model.onnx"),
            validModel ? Convert.FromBase64String(AddModelBase64) : [1, 2, 3, 4]);
        return directory;
    }

    private static async Task<List<ModelStreamEvent>> ReadStreamAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

}
