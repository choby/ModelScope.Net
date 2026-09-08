namespace ModelScope.Net.Hub;

public sealed record FileDownloadRequest(
    ModelId ModelId,
    string FilePath,
    string DestinationPath,
    string? Revision = null,
    long? ExpectedSize = null,
    string? ExpectedSha256 = null);

public sealed record SnapshotDownloadRequest(
    ModelId ModelId,
    string? Revision = null,
    string? LocalDirectory = null,
    IReadOnlyList<string>? AllowPatterns = null,
    IReadOnlyList<string>? IgnorePatterns = null,
    int? MaxWorkers = null,
    bool LocalFilesOnly = false);
