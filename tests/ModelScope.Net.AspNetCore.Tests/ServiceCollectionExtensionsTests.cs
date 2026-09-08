using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelScope.Net.AspNetCore;
using ModelScope.Net.Hub;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Gguf;
using ModelScope.Net.Runtime.Python;
using ModelScope.Net.Runtime.Remote;

namespace ModelScope.Net.AspNetCore.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddModelScopeNet_RegistersHubRuntimesRouterAndHealthyHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModelScopeNet();
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IModelScopeHubClient>());
        Assert.NotNull(provider.GetRequiredService<RuntimeRouter>());
        Assert.NotNull(provider.GetRequiredService<ModelSessionPool>());
        Assert.NotNull(provider.GetRequiredService<ModelScopeTelemetry>());
        Assert.NotNull(provider.GetRequiredService<IModelScopeAuditSink>());
        Assert.NotNull(provider.GetRequiredService<IModelSessionInstrumentation>());
        var runtimes = provider.GetServices<IModelRuntime>().Select(runtime => runtime.Name).ToArray();
        Assert.Contains("remote", runtimes);
        Assert.Contains("python", runtimes);
        Assert.Contains("onnx", runtimes);
        Assert.Contains("onnx-text-generation", runtimes);
        Assert.Contains("onnx-embedding", runtimes);
        Assert.Contains("onnx-text-classification", runtimes);
        Assert.Contains("onnx-image-classification", runtimes);
        Assert.Contains("gguf", runtimes);

        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(HealthStatus.Healthy, health.Entries["modelscope-net"].Status);
        Assert.Equal(HealthStatus.Healthy, health.Entries["modelscope-python-worker"].Status);
        Assert.Equal(HealthStatus.Healthy, health.Entries["modelscope-resource-pool"].Status);
        Assert.Equal(0, health.Entries["modelscope-resource-pool"].Data["cachedSessions"]);
    }

    [Fact]
    public async Task AddModelScopeNet_ConfiguresResourcePoolLimits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModelScopeNet(configureResources: options =>
        {
            options.MaxCachedSessions = 2;
            options.MaxEstimatedMemoryBytes = 512 * 1024 * 1024;
            options.MaxConcurrentLeases = 3;
            options.MaxConcurrentLeasesPerModel = 1;
            options.MaxQueuedAcquisitions = 7;
        });
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ModelSessionPoolOptions>>().Value;
        var pool = provider.GetRequiredService<ModelSessionPool>();
        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(2, options.MaxCachedSessions);
        Assert.Equal(512 * 1024 * 1024, options.MaxEstimatedMemoryBytes);
        Assert.Equal(3, options.MaxConcurrentLeases);
        Assert.Equal(7, options.MaxQueuedAcquisitions);
        Assert.Empty(pool.GetSnapshot().Entries);
        Assert.Equal(2, health.Entries["modelscope-resource-pool"].Data["maxCachedSessions"]);
    }

    [Fact]
    public async Task AddModelScopeNet_WithLlamaSupervisor_RegistersShutdownLifecycle()
    {
        var supervisor = new FakeLlamaSupervisor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModelScopeNet(createLlamaSupervisor: _ => supervisor);
        await using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<LlamaServerHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(1, supervisor.StopCalls);
    }

    [Fact]
    public async Task AddModelScopeNet_WithSupervisor_RegistersHostedLifecycle()
    {
        var supervisor = new FakeSupervisor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModelScopeNet(createPythonSupervisor: _ => supervisor);
        await using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<PythonWorkerHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(1, supervisor.StartCalls);
        Assert.Equal(1, supervisor.StopCalls);
    }

    private sealed class FakeSupervisor : IPythonWorkerSupervisor
    {
        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLlamaSupervisor : ILlamaServerSupervisor
    {
        public Uri Endpoint { get; } = new("http://127.0.0.1:18080/");

        public string ModelAlias => "test";

        public string ApiKey => "test-key";

        public int StopCalls { get; private set; }

        public Task EnsureRunningAsync(string modelFile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public LlamaServerProcessStatus GetStatus() => new(
            false,
            null,
            null,
            null,
            Endpoint,
            []);
    }
}
