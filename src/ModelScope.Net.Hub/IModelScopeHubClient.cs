namespace ModelScope.Net.Hub;

public interface IModelScopeHubClient
{
    Task<string> ResolveModelRevisionAsync(ModelId modelId, string? revision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HubModelRevision>> GetModelRevisionsAsync(
        ModelId modelId, CancellationToken cancellationToken = default);

    Task<HubModelInfo> GetModelAsync(
        ModelId modelId,
        string? revision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelFileInfo>> GetModelFilesAsync(
        ModelId modelId,
        string? revision = null,
        bool recursive = true,
        CancellationToken cancellationToken = default);

    Task<string> DownloadFileAsync(
        FileDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModelSnapshot> DownloadSnapshotAsync(
        SnapshotDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
