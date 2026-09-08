using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelScope.Net.Runtime;

public sealed record CompatibilityCatalog(
    int SchemaVersion,
    string CatalogVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CompatibilityCatalogEntry> Models)
{
    public IReadOnlySet<string> GetCertifiedModels(string runtimeName) => Models
        .Where(model =>
            model.Status == CapabilityStatus.Certified &&
            string.Equals(model.Runtime, runtimeName, StringComparison.OrdinalIgnoreCase))
        .Select(model => model.ModelId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record CompatibilityCatalogEntry(
    string ModelId,
    string Revision,
    string Task,
    string Runtime,
    CapabilityStatus Status,
    IReadOnlyDictionary<string, string>? ArtifactSha256 = null,
    IReadOnlyList<string>? Platforms = null,
    string? RuntimeSha256 = null);

public static class CompatibilityCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<CompatibilityCatalog> LoadAndVerifyAsync(
        string catalogPath,
        string signaturePath,
        string publicKeyPem,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        var content = await File.ReadAllBytesAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        var signatureText = await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException exception)
        {
            throw IntegrityFailure("Catalog signature is not valid Base64.", exception);
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException exception)
        {
            throw IntegrityFailure("Catalog public key is invalid.", exception);
        }

        if (!rsa.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw IntegrityFailure("Catalog signature verification failed.");
        }

        CompatibilityCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<CompatibilityCatalog>(content, JsonOptions)
                ?? throw new JsonException("Catalog is empty.");
        }
        catch (JsonException exception)
        {
            throw IntegrityFailure("Catalog JSON is invalid.", exception);
        }

        Validate(catalog);
        return catalog;
    }

    private static void Validate(CompatibilityCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(catalog.CatalogVersion))
        {
            throw IntegrityFailure("Catalog schemaVersion must be 1 and catalogVersion is required.");
        }

        if (catalog.Models is null)
        {
            throw IntegrityFailure("Catalog models collection is required.");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (!ModelId.TryParse(model.ModelId, out _))
            {
                throw IntegrityFailure($"Catalog contains invalid Model ID '{model.ModelId}'.");
            }

            if (!IsCommitHash(model.Revision))
            {
                throw IntegrityFailure($"Catalog model '{model.ModelId}' must use a fixed hexadecimal Commit revision.");
            }

            if (string.IsNullOrWhiteSpace(model.Task) || string.IsNullOrWhiteSpace(model.Runtime))
            {
                throw IntegrityFailure($"Catalog model '{model.ModelId}' requires task and runtime.");
            }

            if (!keys.Add($"{model.ModelId}@{model.Revision}:{model.Runtime}:{model.Task}"))
            {
                throw IntegrityFailure($"Catalog contains a duplicate entry for '{model.ModelId}'.");
            }

            if (model.ArtifactSha256 is not null && model.ArtifactSha256.Values.Any(hash => !IsSha256(hash)))
            {
                throw IntegrityFailure($"Catalog model '{model.ModelId}' contains an invalid artifact SHA-256.");
            }
        }
    }

    private static bool IsCommitHash(string value) =>
        value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static ModelScopeException IntegrityFailure(string message, Exception? inner = null) =>
        new(ModelScopeErrorCode.DownloadIntegrityFailed, message, false, inner);
}
