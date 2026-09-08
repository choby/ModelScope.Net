using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ModelScope.Net.Runtime.Python;

public static class PythonWorkerHttpHandlerFactory
{
    public static HttpMessageHandler Create(PythonWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PythonWorkerSecurity.ValidateOptions(options);

        var handler = new SocketsHttpHandler();
        if (!options.Tls.IsConfigured) return handler;

        var clientCertificatePath = RequireFile(options.Tls.ClientCertificatePath, "client certificate");
        var clientPrivateKeyPath = RequireFile(options.Tls.ClientPrivateKeyPath, "client private key");
        var trustedCaPath = RequireFile(options.Tls.TrustedCaCertificatePath, "trusted CA certificate");
        using var clientPublicCertificate = X509CertificateLoader.LoadCertificateFromFile(clientCertificatePath);
        using var clientPrivateKey = RSA.Create();
        clientPrivateKey.ImportFromPem(File.ReadAllText(clientPrivateKeyPath));
        var clientCertificate = clientPublicCertificate.CopyWithPrivateKey(clientPrivateKey);
        var trustedCa = X509CertificateLoader.LoadCertificateFromFile(trustedCaPath);

        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            ValidateServerCertificate(certificate, errors, trustedCa);
        return handler;
    }

    private static bool ValidateServerCertificate(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        X509Certificate2 trustedCa)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;

        using var serverCertificate = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(serverCertificate);
    }

    private static string RequireFile(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"The Python worker {description} file is not available.");
        }

        return Path.GetFullPath(path);
    }
}
