namespace ModelScope.Net.Runtime.Python;

public enum PythonWorkerTransport
{
    Http,
    Grpc,
}

public sealed class PythonWorkerOptions
{
    public Uri? Endpoint { get; set; }

    public PythonWorkerTransport Transport { get; set; } = PythonWorkerTransport.Http;

    public bool AllowRemoteCode { get; set; }

    public string? ApiKey { get; set; }

    public PythonWorkerTlsOptions Tls { get; } = new();

    public int RecoveryAttempts { get; set; } = 1;

    public TimeSpan RecoveryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public ISet<string> CertifiedModels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class PythonWorkerTlsOptions
{
    public string? ClientCertificatePath { get; set; }

    public string? ClientPrivateKeyPath { get; set; }

    public string? TrustedCaCertificatePath { get; set; }

    internal bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientCertificatePath) &&
        !string.IsNullOrWhiteSpace(ClientPrivateKeyPath) &&
        !string.IsNullOrWhiteSpace(TrustedCaCertificatePath);
}

public sealed record PythonWorkerHealth(bool IsHealthy, string? Version, string? Message);

public interface IPythonWorkerRuntime : IModelRuntime
{
    Task<PythonWorkerHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public interface IPythonWorkerSupervisor
{
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IPythonWorkerSecurityContext
{
    string ApiKey { get; }
}
