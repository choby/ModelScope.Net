namespace ModelScope.Net;

public sealed record HubModelInfo(
    ModelId ModelId,
    string? Revision,
    string? Commit,
    string? Description,
    string? License,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ModelFileInfo> Files);

public sealed record ModelFileInfo(
    string Path,
    long Size,
    string? Sha256,
    string? Revision,
    string Type,
    DateTimeOffset? LastModified = null)
{
    public bool IsFile => string.Equals(Type, "blob", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Type, "tree", StringComparison.OrdinalIgnoreCase);
}

public sealed record SnapshotManifest(
    ModelId ModelId,
    string RequestedRevision,
    string ResolvedRevision,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SnapshotFile> Files);

public sealed record SnapshotFile(
    string Path,
    long Size,
    string? Sha256,
    string BlobPath);

public sealed record ModelSnapshot(
    ModelId ModelId,
    string Revision,
    string Directory,
    string ManifestPath,
    IReadOnlyList<ModelFileInfo> Files);

public sealed record DownloadProgress(
    string Path,
    long BytesReceived,
    long? TotalBytes,
    int CompletedFiles = 0,
    int TotalFiles = 1);
