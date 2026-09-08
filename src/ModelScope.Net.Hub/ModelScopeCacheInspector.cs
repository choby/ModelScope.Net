using System.Security.Cryptography;
using System.Text.Json;

namespace ModelScope.Net.Hub;

public sealed record ModelScopeCacheSummary(
    string CacheDirectory,
    int BlobCount,
    long BlobBytes,
    int SnapshotCount,
    int IncompleteFileCount);

public sealed record ModelScopeCacheIssue(string Path, string Code, string Message);

public sealed record ModelScopeCacheVerification(
    ModelScopeCacheSummary Summary,
    IReadOnlyList<ModelScopeCacheIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class ModelScopeCacheInspector
{
    private readonly string _cacheDirectory;

    public ModelScopeCacheInspector(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public ModelScopeCacheSummary Scan()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return new ModelScopeCacheSummary(_cacheDirectory, 0, 0, 0, 0);
        }

        var files = Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories).ToArray();
        var blobsRoot = Path.Combine(_cacheDirectory, "blobs") + Path.DirectorySeparatorChar;
        var blobs = files.Where(path =>
            Path.GetFullPath(path).StartsWith(blobsRoot, StringComparison.Ordinal) &&
            !path.EndsWith(".lock", StringComparison.Ordinal) &&
            !path.EndsWith(".incomplete", StringComparison.Ordinal)).ToArray();
        return new ModelScopeCacheSummary(
            _cacheDirectory,
            blobs.Length,
            blobs.Sum(path => new FileInfo(path).Length),
            files.Count(path => string.Equals(
                Path.GetFileName(path),
                ".modelscope-net-manifest.json",
                StringComparison.Ordinal)),
            files.Count(path => path.EndsWith(".incomplete", StringComparison.Ordinal)));
    }

    public async Task<ModelScopeCacheVerification> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        var summary = Scan();
        if (!Directory.Exists(_cacheDirectory))
        {
            return new ModelScopeCacheVerification(summary, []);
        }

        var issues = new List<ModelScopeCacheIssue>();
        var manifests = Directory.EnumerateFiles(
            _cacheDirectory,
            ".modelscope-net-manifest.json",
            SearchOption.AllDirectories);
        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotManifest? manifest;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(
                    stream,
                    HubJson.Options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                issues.Add(new ModelScopeCacheIssue(manifestPath, "InvalidManifest", exception.Message));
                continue;
            }

            if (manifest is null)
            {
                issues.Add(new ModelScopeCacheIssue(manifestPath, "InvalidManifest", "Manifest is empty."));
                continue;
            }

            var snapshotRoot = Path.GetDirectoryName(manifestPath)!;
            foreach (var file in manifest.Files)
            {
                string snapshotPath;
                try
                {
                    snapshotPath = SafePath.CombineUnderRoot(snapshotRoot, file.Path);
                }
                catch (ModelScopeException exception)
                {
                    issues.Add(new ModelScopeCacheIssue(file.Path, "UnsafePath", exception.Message));
                    continue;
                }

                if (!File.Exists(snapshotPath))
                {
                    issues.Add(new ModelScopeCacheIssue(snapshotPath, "MissingFile", "Snapshot file is missing."));
                    continue;
                }

                await using var stream = File.OpenRead(snapshotPath);
                if (file.Size > 0 && stream.Length != file.Size)
                {
                    issues.Add(new ModelScopeCacheIssue(snapshotPath, "SizeMismatch", $"Expected {file.Size}, found {stream.Length}."));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(file.Sha256))
                {
                    var actual = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ModelScopeCacheIssue(snapshotPath, "HashMismatch", "SHA-256 does not match the manifest."));
                    }
                }
            }
        }

        return new ModelScopeCacheVerification(summary, issues);
    }
}
