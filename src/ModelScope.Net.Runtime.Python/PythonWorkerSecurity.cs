using System.Net;
using System.Net.Http.Headers;
using Grpc.Core;

namespace ModelScope.Net.Runtime.Python;

internal static class PythonWorkerSecurity
{
    public static void ValidateOptions(PythonWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint is null) return;
        ValidateEndpoint(options.Endpoint);

        var tls = options.Tls;
        var configuredValues = new[]
        {
            tls.ClientCertificatePath,
            tls.ClientPrivateKeyPath,
            tls.TrustedCaCertificatePath,
        }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (configuredValues is > 0 and < 3)
        {
            throw new ArgumentException(
                "Python worker mTLS requires a client certificate, client private key and trusted CA certificate.",
                nameof(options));
        }

        if (tls.IsConfigured && options.Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Python worker mTLS can only be used with an HTTPS endpoint.", nameof(options));
        }

        if (!options.Endpoint.IsLoopback && !tls.IsConfigured)
        {
            throw new ArgumentException(
                "A remote Python worker requires mutual TLS client credentials and a trusted CA certificate.",
                nameof(options));
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Python worker endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp &&
            (!IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException(
                "Clear-text Python worker endpoints are allowed only on an IP loopback address; use HTTPS for remote workers.",
                nameof(endpoint));
        }
    }

    public static string? ResolveApiKey(PythonWorkerOptions options, IPythonWorkerSupervisor? supervisor)
    {
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? (supervisor as IPythonWorkerSecurityContext)?.ApiKey
            : options.ApiKey;
        if (apiKey is not null && apiKey.Any(char.IsControl))
        {
            throw new ArgumentException("Python worker API key cannot contain control characters.", nameof(options));
        }

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public static Metadata? CreateGrpcHeaders(string? apiKey) => apiKey is null
        ? null
        : new Metadata { { "authorization", $"Bearer {apiKey}" } };

    public static void ApplyHttpAuthorization(HttpRequestMessage message, string? apiKey)
    {
        if (apiKey is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }
}
