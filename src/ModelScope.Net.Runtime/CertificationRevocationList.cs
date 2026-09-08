using System.Security.Cryptography;
using System.Text.Json;

namespace ModelScope.Net.Runtime;

public sealed record CertificationRevocation(
    string ModelId, string Revision, string Task, string Runtime, string RuntimeSha256);

public sealed record CertificationRevocationList(
    int SchemaVersion, string CatalogVersion, long Sequence,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<CertificationRevocation> Revoked);

public sealed record RevocationCheckpointAnchor(long Sequence, string Sha256);

/// <summary>Append-only revocations scoped to a Catalog and its existing trust root.</summary>
internal sealed class CertificationRevocationState(string publicKeyPem, string catalogVersion, TimeProvider clock)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _updates = new(1, 1);
    private bool _publicationRequired;
    private bool _publicationPending;
    private bool _storeStagingPending;
    private bool _storeRequired;
    private PendingRevocationPublication? _pendingStoreUpdate;
    private readonly HashSet<CertificationRevocation> _revoked = [];
    private long _sequence;
    private DateTimeOffset? _expiresAt;
    private readonly List<SignedRevocationRecord> _history = [];

    internal sealed record SignedRevocationRecord(byte[] Document, byte[] Signature);
    internal sealed record Checkpoint(int SchemaVersion, List<SignedRevocationRecord> Records);

    public bool IsUnavailable
    {
        get { lock (_sync) return _publicationPending || _expiresAt is null || clock.GetUtcNow() >= _expiresAt.Value; }
    }

    public bool IsBlocked(string modelId, string revision, string task, string runtime, string digest)
    {
        lock (_sync)
            return IsUnavailable ||
                _revoked.Contains(Normalize(new(modelId, revision, task, runtime, digest)));
    }

    public async Task ApplyAsync(string path, string signaturePath, CancellationToken cancellationToken)
    {
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publicationRequired) throw new InvalidOperationException("This policy requires durable revocation publication.");
            var bytes = await ReadBoundedAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var signature = await ReadBoundedAsync(signaturePath, 16384, cancellationToken).ConfigureAwait(false);
            ApplyVerified(bytes, signature, historical: false);
        }
        finally { _updates.Release(); }
    }

    public async Task<RevocationCheckpointAnchor> ApplyAndPublishAsync(string path, string signaturePath,
        string checkpointPath, Func<string, RevocationCheckpointAnchor, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) if (_storeRequired) throw new InvalidOperationException("This policy requires its durable publication store.");
            var bytes = await ReadBoundedAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var signature = await ReadBoundedAsync(signaturePath, 16384, cancellationToken).ConfigureAwait(false);
            ApplyVerified(bytes, signature, historical: false, requirePublication: true);
            return await PublishCoreAsync(checkpointPath, publish, cancellationToken).ConfigureAwait(false);
        }
        finally { _updates.Release(); }
    }

    public async Task<RevocationCheckpointAnchor> RetryPublicationAsync(string checkpointPath,
        Func<string, RevocationCheckpointAnchor, CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storeRequired) throw new InvalidOperationException("This policy requires its durable publication store.");
            if (!_publicationRequired || !_publicationPending) throw new InvalidOperationException("No revocation publication is pending.");
            if (_storeStagingPending) throw new InvalidOperationException("The durable revocation update must be staged before checkpoint publication can retry.");
            return await PublishCoreAsync(checkpointPath, publish, cancellationToken).ConfigureAwait(false);
        }
        finally { _updates.Release(); }
    }

    public async Task<RevocationCheckpointAnchor> ApplyAndPublishAsync(string path, string signaturePath,
        string checkpointPath, IRevocationPublicationStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await ReadBoundedAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var signature = await ReadBoundedAsync(signaturePath, 16384, cancellationToken).ConfigureAwait(false);
            var list = Verify(bytes, signature, historical: false);
            EnsureNextSequence(list.Sequence);
            var pending = new PendingRevocationPublication(list.Sequence, bytes, signature);
            lock (_sync)
            {
                _publicationRequired = true;
                _publicationPending = true;
                _storeStagingPending = true;
                _storeRequired = true;
                _pendingStoreUpdate = pending;
            }
            await store.StageAsync(pending, cancellationToken).ConfigureAwait(false);
            ApplyVerified(list, bytes, signature, requirePublication: true);
            lock (_sync) _storeStagingPending = false;
            var fullPath = Path.GetFullPath(checkpointPath);
            var anchor = await SaveCheckpointAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(pending, new(fullPath, anchor), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _publicationPending = false;
                _pendingStoreUpdate = null;
            }
            return anchor;
        }
        finally { _updates.Release(); }
    }

    public async Task<RevocationCheckpointAnchor> RetryPublicationAsync(string checkpointPath,
        IRevocationPublicationStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingRevocationPublication pending;
            lock (_sync)
            {
                if (!_storeRequired || !_publicationPending || _storeStagingPending || _pendingStoreUpdate is null)
                    throw new InvalidOperationException("No store-backed revocation publication is ready to retry.");
                pending = _pendingStoreUpdate;
            }
            var fullPath = Path.GetFullPath(checkpointPath);
            var anchor = await SaveCheckpointAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(pending, new(fullPath, anchor), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _publicationPending = false;
                _pendingStoreUpdate = null;
            }
            return anchor;
        }
        finally { _updates.Release(); }
    }

    public async Task ReconcilePublicationStoreAsync(IRevocationPublicationStore store,
        Func<long, string> createCheckpointPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(createCheckpointPath);
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _publicationRequired = true;
                _storeRequired = true;
            }
            var state = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Committed is not null)
                await RestoreCheckpointAsync(state.Committed.CheckpointPath, state.Committed.Anchor, cancellationToken).ConfigureAwait(false);
            if (state.Pending is null) return;

            lock (_sync)
            {
                _publicationPending = true;
                _storeStagingPending = false;
                _pendingStoreUpdate = state.Pending;
            }
            var list = Verify(state.Pending.Document, state.Pending.Signature, historical: false);
            if (list.Sequence != state.Pending.Sequence) throw Failure();
            ApplyVerified(list, state.Pending.Document, state.Pending.Signature, requirePublication: true);
            var path = Path.GetFullPath(createCheckpointPath(list.Sequence));
            var anchor = await SaveCheckpointAsync(path, cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(state.Pending, new(path, anchor), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _publicationPending = false;
                _pendingStoreUpdate = null;
            }
        }
        finally { _updates.Release(); }
    }

    private async Task<RevocationCheckpointAnchor> PublishCoreAsync(string checkpointPath,
        Func<string, RevocationCheckpointAnchor, CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        // A failed/uncertain write or publication keeps all authorization blocked.
        var fullPath = Path.GetFullPath(checkpointPath);
        var anchor = await SaveCheckpointAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await publish(fullPath, anchor, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) _publicationPending = false;
        return anchor;
    }

    private CertificationRevocationList Verify(byte[] bytes, byte[] signature, bool historical)
    {
        if (bytes.Length > 1024 * 1024 || signature.Length > 16384) throw Failure();
        CertificationRevocationList list;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (!rsa.VerifyData(bytes, Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(signature).Trim()),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure();
            list = JsonSerializer.Deserialize<CertificationRevocationList>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw Failure();
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            throw Failure();
        }
        var now = clock.GetUtcNow();
        if (list.SchemaVersion != 1 || list.CatalogVersion != catalogVersion || list.Sequence <= 0 ||
            list.IssuedAt > now || (!historical && list.ExpiresAt <= now) || list.ExpiresAt <= list.IssuedAt ||
            list.Revoked is null || list.Revoked.Any(item => item is null ||
                !ModelId.TryParse(item.ModelId, out _) || item.Revision is not { Length: 40 } ||
                !item.Revision.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(item.Task) ||
                string.IsNullOrWhiteSpace(item.Runtime) || item.RuntimeSha256 is not { Length: 71 } ||
                !item.RuntimeSha256.StartsWith("sha256:", StringComparison.Ordinal) ||
                !item.RuntimeSha256[7..].All(Uri.IsHexDigit)))
            throw Failure();
        return list;
    }

    private void EnsureNextSequence(long sequence)
    {
        lock (_sync) if (sequence <= _sequence) throw Failure();
    }

    private void ApplyVerified(byte[] bytes, byte[] signature, bool historical, bool requirePublication = false) =>
        ApplyVerified(Verify(bytes, signature, historical), bytes, signature, requirePublication);

    private void ApplyVerified(CertificationRevocationList list, byte[] bytes, byte[] signature,
        bool requirePublication = false)
    {
        lock (_sync)
        {
            if (list.Sequence <= _sequence) throw Failure();
            // A later valid list cannot reinstate an entry by omitting an earlier revocation.
            foreach (var item in list.Revoked) _revoked.Add(Normalize(item));
            _sequence = list.Sequence;
            _expiresAt = list.ExpiresAt;
            _history.Add(new(bytes, signature));
            if (requirePublication)
            {
                _publicationRequired = true;
                _publicationPending = true;
                _storeStagingPending = false;
            }
        }
    }

    public async Task<RevocationCheckpointAnchor> SaveCheckpointAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        long sequence;
        lock (_sync)
        {
            if (_sequence == 0 || _history.Count > 4096) throw Failure();
            bytes = JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(1, [.. _history]));
            sequence = _sequence;
        }
        if (bytes.Length > 16 * 1024 * 1024) throw Failure();
        // Immutable generation: never overwrite a previous recovery point. A partial file is rejected on load.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough | FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failure(); }
        return new(sequence, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public async Task RestoreCheckpointAsync(string path, RevocationCheckpointAnchor anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.Sequence <= 0 || anchor.Sha256 is not { Length: 64 } || !anchor.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A trusted checkpoint sequence and SHA-256 are required.", nameof(anchor));
        var bytes = await ReadBoundedAsync(path, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), anchor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Failure();
        Checkpoint checkpoint;
        try { checkpoint = JsonSerializer.Deserialize<Checkpoint>(bytes) ?? throw Failure(); }
        catch (JsonException) { throw Failure(); }
        if (checkpoint.SchemaVersion != 1 || checkpoint.Records is null ||
            checkpoint.Records.Count is 0 or > 4096) throw Failure();
        foreach (var record in checkpoint.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record?.Document is null || record.Signature is null) throw Failure();
            ApplyVerified(record.Document, record.Signature, historical: true);
        }
        if (_sequence != anchor.Sequence) throw Failure();
    }

    private static CertificationRevocation Normalize(CertificationRevocation item) => item with
    {
        Revision = item.Revision.ToLowerInvariant(),
        Runtime = item.Runtime.ToLowerInvariant(),
        RuntimeSha256 = item.RuntimeSha256.ToLowerInvariant(),
    };

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var buffer = new byte[maximum + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) throw Failure();
            return buffer[..count];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failure(); }
    }

    private static ModelScopeException Failure() =>
        new(ModelScopeErrorCode.DownloadIntegrityFailed, "The revocation list failed signature, scope, freshness or monotonic-sequence validation.");
}
