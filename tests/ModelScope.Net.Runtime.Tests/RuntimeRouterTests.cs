using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class RuntimeRouterTests
{
    [Fact]
    public async Task CreateSessionAsync_PrefersHighestPriorityAvailableRuntime()
    {
        var unavailable = new FakeRuntime("onnx", CapabilityStatus.Unsupported, 100);
        var available = new FakeRuntime("python", CapabilityStatus.Compatible, 50);
        var router = new RuntimeRouter([unavailable, available]);
        var capabilities = CreateCapabilities(containsRemoteCode: false);

        var session = await router.CreateSessionAsync(capabilities);

        Assert.Same(available.Session, session);
        Assert.Equal(["onnx", "python"], router.Evaluate(capabilities).Select(candidate => candidate.RuntimeName));
    }

    [Fact]
    public async Task CreateSessionAsync_RejectsRemoteCodeWhenPolicyDisallowsIt()
    {
        var runtime = new FakeRuntime("python", CapabilityStatus.Compatible, 50);
        var router = new RuntimeRouter([runtime], new RuntimeRouterOptions { AllowRemoteCode = false });
        var capabilities = CreateCapabilities(containsRemoteCode: true);

        var error = await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(capabilities));

        Assert.Equal(ModelScopeErrorCode.RemoteCodeNotAllowed, error.Code);
    }

    [Fact]
    public async Task CreateSessionAsync_DevelopmentModeFallsBackAfterRetryableLoadFailure()
    {
        var failing = new FakeRuntime(
            "onnx",
            CapabilityStatus.Compatible,
            100,
            new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "test failure", isRetryable: true));
        var fallback = new FakeRuntime("python", CapabilityStatus.Detected, 50);
        var router = new RuntimeRouter([failing, fallback]);

        var session = await router.CreateSessionAsync(CreateCapabilities(containsRemoteCode: false));

        Assert.Same(fallback.Session, session);
    }

    [Fact]
    public async Task CreateSessionAsync_FallsBackToPythonWhenOnnxLoadFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-router-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(directory, "model.safetensors"), [0]);
            var capabilities = await new ModelInspector().InspectAsync(directory);
            var python = new FakeRuntime("python", CapabilityStatus.Detected, 50);
            var router = new RuntimeRouter([new OnnxRuntimeAdapter(), python]);

            var session = await router.CreateSessionAsync(capabilities);

            Assert.Same(python.Session, session);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_DoesNotFallBackWhenLoadFailureIsNotRetryable()
    {
        var failing = new FakeRuntime(
            "onnx",
            CapabilityStatus.Compatible,
            100,
            new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "not retryable"));
        var fallback = new FakeRuntime("python", CapabilityStatus.Detected, 50);
        var router = new RuntimeRouter([failing, fallback]);

        var error = await Assert.ThrowsAsync<ModelScopeException>(
            () => router.CreateSessionAsync(CreateCapabilities(containsRemoteCode: false)));

        Assert.Equal(ModelScopeErrorCode.ModelLoadFailed, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task CreateSessionAsync_ProductionRejectsUncertifiedRuntimeByDefault()
    {
        var runtime = new FakeRuntime("onnx", CapabilityStatus.Compatible, 100);
        var router = new RuntimeRouter(
            [runtime],
            new RuntimeRouterOptions { Mode = RuntimePolicyMode.Production });

        var error = await Assert.ThrowsAsync<ModelScopeException>(() =>
            router.CreateSessionAsync(CreateCapabilities(containsRemoteCode: false)));

        Assert.Equal(ModelScopeErrorCode.RuntimeNotInstalled, error.Code);
    }

    private static ModelCapabilities CreateCapabilities(bool containsRemoteCode)
    {
        return new ModelCapabilities(
            "/tmp/model",
            "owner/model",
            "master",
            "text-generation",
            [],
            [],
            [],
            containsRemoteCode,
            []);
    }

    private sealed class FakeRuntime(
        string name,
        CapabilityStatus status,
        int priority,
        ModelScopeException? failure = null) : IModelRuntime
    {
        public string Name => name;

        public IModelSession Session { get; } = new FakeSession();

        public RuntimeCandidate Evaluate(ModelCapabilities capabilities) =>
            new(name, status, "test", priority);

        public Task<IModelSession> CreateSessionAsync(
            ModelCapabilities capabilities,
            CancellationToken cancellationToken = default) => failure is null
                ? Task.FromResult(Session)
                : Task.FromException<IModelSession>(failure);
    }

    private sealed class FakeSession : IModelSession
    {
        public ModelCapabilities Capabilities => CreateCapabilities(containsRemoteCode: false);

        public Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
