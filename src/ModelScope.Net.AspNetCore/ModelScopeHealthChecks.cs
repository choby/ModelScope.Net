using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ModelScope.Net.Hub;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.AspNetCore;

public sealed class ModelScopeNetHealthCheck(
    IOptions<ModelScopeHubOptions> hubOptions,
    IEnumerable<IModelRuntime> runtimes) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpoint = hubOptions.Value.Endpoint;
        var runtimeNames = runtimes.Select(runtime => runtime.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!endpoint.IsAbsoluteUri || runtimeNames.Length == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("ModelScope.Net configuration is incomplete."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "ModelScope.Net services are registered.",
            new Dictionary<string, object>
            {
                ["hubEndpoint"] = endpoint.GetLeftPart(UriPartial.Authority),
                ["runtimes"] = runtimeNames,
            }));
    }
}

public sealed class PythonWorkerHealthCheck(
    IPythonWorkerRuntime runtime,
    IOptions<PythonWorkerOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Endpoint is null)
        {
            return HealthCheckResult.Healthy("Python Worker is not configured and is optional.");
        }

        try
        {
            var health = await runtime.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return health.IsHealthy
                ? HealthCheckResult.Healthy(
                    health.Message ?? "Python Worker is healthy.",
                    new Dictionary<string, object> { ["version"] = health.Version ?? "unknown" })
                : HealthCheckResult.Degraded(health.Message ?? "Python Worker is unhealthy.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Python Worker health request failed.", exception);
        }
    }
}

public sealed class ModelSessionPoolHealthCheck(
    ModelSessionPool pool,
    IOptions<ModelSessionPoolOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = pool.GetSnapshot();
        var limits = options.Value;
        var data = new Dictionary<string, object>
        {
            ["cachedSessions"] = snapshot.CachedSessions,
            ["maxCachedSessions"] = limits.MaxCachedSessions,
            ["activeLeases"] = snapshot.ActiveLeases,
            ["maxConcurrentLeases"] = limits.MaxConcurrentLeases,
            ["queuedAcquisitions"] = snapshot.QueuedAcquisitions,
            ["maxQueuedAcquisitions"] = limits.MaxQueuedAcquisitions,
            ["estimatedMemoryBytes"] = snapshot.EstimatedMemoryBytes,
            ["maxEstimatedMemoryBytes"] = limits.MaxEstimatedMemoryBytes,
        };

        return Task.FromResult(HealthCheckResult.Healthy(
            "Model session resource pool is within configured limits.",
            data));
    }
}
