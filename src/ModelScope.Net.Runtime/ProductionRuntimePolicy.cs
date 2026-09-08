using System.Security.Cryptography;

namespace ModelScope.Net.Runtime;

/// <summary>Signed Catalog snapshot bound to one deployed platform, with live append-only revocations.</summary>
public sealed class ProductionRuntimePolicy
{
    private readonly CompatibilityCatalog _catalog;
    private readonly string _platform;
    private readonly IReadOnlyDictionary<string, string> _runtimeDigests;
    private readonly CertificationRevocationState _revocations;

    private ProductionRuntimePolicy(CompatibilityCatalog catalog, string platform,
        IReadOnlyDictionary<string, string> runtimeDigests, string publicKeyPem, TimeProvider clock)
    {
        _catalog = catalog;
        _platform = platform;
        _runtimeDigests = runtimeDigests;
        _revocations = new(publicKeyPem, catalog.CatalogVersion, clock);
    }

    public string CatalogVersion => _catalog.CatalogVersion;

    public static async Task<ProductionRuntimePolicy> LoadAsync(string catalogPath, string signaturePath,
        string publicKeyPem, string platform, IReadOnlyDictionary<string, string> runtimeDigests,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(runtimeDigests);
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (runtime, digest) in runtimeDigests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
            if (!IsDigest(digest)) throw new ArgumentException("Runtime digests must use sha256:<64 hex digits>.", nameof(runtimeDigests));
            pinned.Add(runtime, digest);
        }
        // Loader returns a fresh object which never escapes this policy; caller collections cannot mutate authorization.
        var catalog = await CompatibilityCatalogLoader.LoadAndVerifyAsync(
            catalogPath, signaturePath, publicKeyPem, cancellationToken).ConfigureAwait(false);
        return new(catalog, platform, pinned, publicKeyPem, timeProvider ?? TimeProvider.System);
    }

    public bool IsCertified(ModelCapabilities capabilities, string runtime) => FindEntry(capabilities, runtime) is not null;

    public Task ApplyRevocationsAsync(string listPath, string signaturePath, CancellationToken cancellationToken = default) =>
        _revocations.ApplyAsync(listPath, signaturePath, cancellationToken);

    /// <summary>Blocks authorization after accepting a signed update until its checkpoint and trusted anchor publication succeed.
    /// The publisher must durably publish the path/anchor to a separate rollback-resistant store and must not reenter policy updates.</summary>
    public Task<RevocationCheckpointAnchor> ApplyAndPublishRevocationsAsync(string listPath, string signaturePath,
        string checkpointPath, Func<string, RevocationCheckpointAnchor, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default) =>
        _revocations.ApplyAndPublishAsync(listPath, signaturePath, checkpointPath, publish, cancellationToken);

    /// <summary>Stages the signed update durably before accepting it, then commits its checkpoint anchor.</summary>
    public Task<RevocationCheckpointAnchor> ApplyAndPublishRevocationsAsync(string listPath, string signaturePath,
        string checkpointPath, IRevocationPublicationStore store, CancellationToken cancellationToken = default) =>
        _revocations.ApplyAndPublishAsync(listPath, signaturePath, checkpointPath, store, cancellationToken);

    /// <summary>Retries a failed/uncertain publication using a new immutable checkpoint path without replaying the signed list.</summary>
    public Task<RevocationCheckpointAnchor> RetryRevocationPublicationAsync(string checkpointPath,
        Func<string, RevocationCheckpointAnchor, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default) =>
        _revocations.RetryPublicationAsync(checkpointPath, publish, cancellationToken);

    /// <summary>Retries committing an accepted store-backed update using a new immutable checkpoint path.</summary>
    public Task<RevocationCheckpointAnchor> RetryRevocationPublicationAsync(string checkpointPath,
        IRevocationPublicationStore store, CancellationToken cancellationToken = default) =>
        _revocations.RetryPublicationAsync(checkpointPath, store, cancellationToken);

    /// <summary>Writes a new immutable checkpoint; publish its returned anchor through a separate trusted deployment channel.</summary>
    public Task<RevocationCheckpointAnchor> SaveRevocationCheckpointAsync(string path, CancellationToken cancellationToken = default) =>
        _revocations.SaveCheckpointAsync(path, cancellationToken);

    public static async Task<ProductionRuntimePolicy> LoadWithRevocationCheckpointAsync(
        string catalogPath, string signaturePath, string publicKeyPem, string platform,
        IReadOnlyDictionary<string, string> runtimeDigests, string checkpointPath, RevocationCheckpointAnchor anchor,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
    {
        var policy = await LoadAsync(catalogPath, signaturePath, publicKeyPem, platform,
            runtimeDigests, cancellationToken, timeProvider).ConfigureAwait(false);
        // Restore into a private new policy: no partially restored authorization can escape on failure.
        await policy._revocations.RestoreCheckpointAsync(checkpointPath, anchor, cancellationToken).ConfigureAwait(false);
        return policy;
    }

    /// <summary>Loads the last committed checkpoint and completes any durably staged update before returning.</summary>
    public static async Task<ProductionRuntimePolicy> LoadWithRevocationPublicationStoreAsync(
        string catalogPath, string signaturePath, string publicKeyPem, string platform,
        IReadOnlyDictionary<string, string> runtimeDigests, IRevocationPublicationStore store,
        Func<long, string> createCheckpointPath, CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(createCheckpointPath);
        var policy = await LoadAsync(catalogPath, signaturePath, publicKeyPem, platform,
            runtimeDigests, cancellationToken, timeProvider).ConfigureAwait(false);
        await policy._revocations.ReconcilePublicationStoreAsync(store, createCheckpointPath, cancellationToken)
            .ConfigureAwait(false);
        return policy;
    }

    public bool IsRevokedOrExpired(ModelCapabilities capabilities, string runtime) =>
        _revocations.IsUnavailable ||
        capabilities.ModelId is not null && capabilities.Revision is not null && capabilities.Task is not null &&
        _runtimeDigests.TryGetValue(runtime, out var digest) &&
        _revocations.IsBlocked(capabilities.ModelId, capabilities.Revision, capabilities.Task, runtime, digest);

    public async Task VerifyArtifactsAsync(ModelCapabilities capabilities, string runtime,
        CancellationToken cancellationToken = default)
    {
        var entry = FindEntry(capabilities, runtime) ??
            throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled,
                "No signed certification matches the model, Commit, task, platform and deployed runtime digest.");
        var hashes = entry.ArtifactSha256;
        // A remote-only entry may have no local artifacts; its service deployment pin remains mandatory.
        if ((hashes is null || hashes.Count == 0) &&
            (capabilities.Artifacts.Count != 0 || !string.Equals(runtime, "remote", StringComparison.OrdinalIgnoreCase)))
            throw IntegrityFailure();
        if (capabilities.Artifacts.Any(a => hashes is null || !hashes.ContainsKey(a.Path))) throw IntegrityFailure();
        if (hashes is null || hashes.Count == 0) return;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(capabilities.ModelPath));
        foreach (var (relative, expected) in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw IntegrityFailure();
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw IntegrityFailure();
            try
            {
                await using var stream = File.OpenRead(path);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw IntegrityFailure();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // File paths and OS messages can contain private model identifiers.
                throw IntegrityFailure();
            }
        }
    }

    private CompatibilityCatalogEntry? FindEntry(ModelCapabilities capabilities, string runtime)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (IsRevokedOrExpired(capabilities, runtime)) return null;
        if (capabilities.Revision is null || capabilities.Revision.Length != 40 || !capabilities.Revision.All(Uri.IsHexDigit) ||
            !_runtimeDigests.TryGetValue(runtime, out var digest)) return null;
        return _catalog.Models.SingleOrDefault(entry =>
            entry.Status == CapabilityStatus.Certified &&
            entry.ModelId == capabilities.ModelId &&
            string.Equals(entry.Revision, capabilities.Revision, StringComparison.OrdinalIgnoreCase) &&
            entry.Task == capabilities.Task &&
            string.Equals(entry.Runtime, runtime, StringComparison.OrdinalIgnoreCase) &&
            entry.Platforms is not null && entry.Platforms.Contains(_platform, StringComparer.Ordinal) &&
            IsDigest(entry.RuntimeSha256) && string.Equals(entry.RuntimeSha256, digest, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDigest(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);
    private static ModelScopeException IntegrityFailure() =>
        new(ModelScopeErrorCode.DownloadIntegrityFailed, "The model snapshot does not match its signed artifact manifest.");
}
