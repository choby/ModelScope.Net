using System.Security.Cryptography;
using System.Text.Json;

namespace ModelScope.Net.Runtime;

public sealed record PendingRevocationPublication(long Sequence, byte[] Document, byte[] Signature);

public sealed record CommittedRevocationPublication(string CheckpointPath, RevocationCheckpointAnchor Anchor);

public sealed record RevocationPublicationStoreState(
    CommittedRevocationPublication? Committed,
    PendingRevocationPublication? Pending);

/// <summary>
/// Durable write-ahead storage for revocation updates and their trusted checkpoint anchor.
/// Implementations must make Stage and Commit atomic, monotonic, idempotent for the same value,
/// and durable before returning. Production implementations must additionally resist rollback.
/// </summary>
public interface IRevocationPublicationStore
{
    Task<RevocationPublicationStoreState> ReadAsync(CancellationToken cancellationToken = default);

    Task StageAsync(PendingRevocationPublication pending, CancellationToken cancellationToken = default);

    Task CommitAsync(
        PendingRevocationPublication pending,
        CommittedRevocationPublication committed,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Single-host atomic file implementation for development and crash testing. The host filesystem
/// is not a rollback-resistant trust root; production must use a protected external implementation.
/// </summary>
public sealed class FileRevocationPublicationStore : IRevocationPublicationStore
{
    private const int MaximumStateBytes = 20 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRevocationPublicationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<RevocationPublicationStoreState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Clone(await ReadCoreAsync(cancellationToken).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    public async Task StageAsync(PendingRevocationPublication pending, CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (state.Pending is not null)
            {
                if (SamePending(state.Pending, pending)) return;
                throw Failure();
            }
            if (state.Committed is not null && pending.Sequence <= state.Committed.Anchor.Sequence) throw Failure();
            await WriteCoreAsync(new(state.Committed, Clone(pending)), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task CommitAsync(PendingRevocationPublication pending, CommittedRevocationPublication committed,
        CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        ValidateCommitted(committed);
        if (pending.Sequence != committed.Anchor.Sequence) throw Failure();
        var checkpointHash = await HashBoundedFileAsync(committed.CheckpointPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(checkpointHash, committed.Anchor.Sha256, StringComparison.OrdinalIgnoreCase)) throw Failure();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (state.Pending is null)
            {
                if (state.Committed is not null && SameCommitted(state.Committed, committed)) return;
                throw Failure();
            }
            if (!SamePending(state.Pending, pending)) throw Failure();
            if (state.Committed is not null && committed.Anchor.Sequence <= state.Committed.Anchor.Sequence) throw Failure();
            await WriteCoreAsync(new(Clone(committed), null), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<RevocationPublicationStoreState> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new(null, null);
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumStateBytes) throw Failure();
            var state = await JsonSerializer.DeserializeAsync<PersistedState>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? throw Failure();
            if (state.SchemaVersion != 1) throw Failure();
            if (state.Pending is not null) ValidatePending(state.Pending);
            if (state.Committed is not null) ValidateCommitted(state.Committed);
            if (state.Pending is not null && state.Committed is not null &&
                state.Pending.Sequence <= state.Committed.Anchor.Sequence) throw Failure();
            return new(Clone(state.Committed), Clone(state.Pending));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Failure();
        }
    }

    private async Task WriteCoreAsync(RevocationPublicationStoreState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw Failure();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new PersistedState(1, state.Committed, state.Pending), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw Failure();
        }
    }

    private static async Task<string> HashBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 16 * 1024 * 1024) throw Failure();
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Failure(); }
    }

    private static void ValidatePending(PendingRevocationPublication? value)
    {
        if (value is null || value.Sequence <= 0 || value.Document is not { Length: > 0 and <= 1024 * 1024 } ||
            value.Signature is not { Length: > 0 and <= 16384 }) throw Failure();
    }

    private static void ValidateCommitted(CommittedRevocationPublication? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.CheckpointPath) || !Path.IsPathFullyQualified(value.CheckpointPath) ||
            value.Anchor is null || value.Anchor.Sequence <= 0 || value.Anchor.Sha256 is not { Length: 64 } ||
            !value.Anchor.Sha256.All(Uri.IsHexDigit)) throw Failure();
    }

    private static bool SamePending(PendingRevocationPublication left, PendingRevocationPublication right) =>
        left.Sequence == right.Sequence && left.Document.AsSpan().SequenceEqual(right.Document) &&
        left.Signature.AsSpan().SequenceEqual(right.Signature);

    private static bool SameCommitted(CommittedRevocationPublication left, CommittedRevocationPublication right) =>
        left.Anchor.Sequence == right.Anchor.Sequence &&
        string.Equals(left.Anchor.Sha256, right.Anchor.Sha256, StringComparison.OrdinalIgnoreCase);

    private static PendingRevocationPublication? Clone(PendingRevocationPublication? value) => value is null
        ? null
        : new(value.Sequence, [.. value.Document], [.. value.Signature]);

    private static CommittedRevocationPublication? Clone(CommittedRevocationPublication? value) => value is null
        ? null
        : new(value.CheckpointPath, value.Anchor with { });

    private static RevocationPublicationStoreState Clone(RevocationPublicationStoreState value) =>
        new(Clone(value.Committed), Clone(value.Pending));

    private static ModelScopeException Failure() => new(ModelScopeErrorCode.DownloadIntegrityFailed,
        "The revocation publication store failed integrity, monotonicity or durability validation.");

    private sealed record PersistedState(int SchemaVersion, CommittedRevocationPublication? Committed,
        PendingRevocationPublication? Pending);
}
