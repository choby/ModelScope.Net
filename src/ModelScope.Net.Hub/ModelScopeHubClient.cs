using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModelScope.Net.Hub;

public sealed partial class ModelScopeHubClient : IModelScopeHubClient
{
    private const int BufferSize = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ModelScopeHubOptions _options;

    public ModelScopeHubClient(HttpClient httpClient, ModelScopeHubOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new ModelScopeHubOptions();
        _options.Validate();
    }

    public async Task<IReadOnlyList<HubModelRevision>> GetModelRevisionsAsync(
        ModelId modelId, CancellationToken cancellationToken = default)
    {
        var uri = BuildUri($"api/v1/models/{Escape(modelId.Owner)}/{Escape(modelId.Name)}/revisions");
        using var response = await SendWithRetryAsync(() => CreateRequest(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, modelId, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = HubJson.UnwrapData(document);
        if (!HubJson.TryGet(data, "RevisionMap", out var map) || map.ValueKind != JsonValueKind.Object)
            throw new ModelScopeException(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid model revision map.");
        var result = new List<HubModelRevision>();
        foreach (var (field, kind) in new[] { ("Branches", HubRevisionKind.Branch), ("Tags", HubRevisionKind.Tag) })
        {
            if (!HubJson.TryGet(map, field, out var entries) || entries.ValueKind == JsonValueKind.Null) continue;
            if (entries.ValueKind != JsonValueKind.Array)
                throw new ModelScopeException(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid model revision list.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var name = HubJson.GetString(entry, "Revision");
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                    throw new ModelScopeException(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid or duplicate model reference.");
                DateTimeOffset? created = null;
                if (HubJson.TryGet(entry, "CreatedAt", out var timestamp) &&
                    long.TryParse(timestamp.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    try { created = value > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value); }
                    catch (ArgumentOutOfRangeException) { /* Unknown timestamp, never substitute a Commit. */ }
                }
                result.Add(new(name, kind, created));
            }
        }
        return result;
    }

    public async Task<HubModelInfo> GetModelAsync(
        ModelId modelId,
        string? revision = null,
        CancellationToken cancellationToken = default)
    {
        var requestedRevision = ResolveRevision(revision);
        revision = await ResolveModelRevisionAsync(modelId, requestedRevision, cancellationToken).ConfigureAwait(false);
        var uri = BuildUri(
            $"api/v1/models/{Escape(modelId.Owner)}/{Escape(modelId.Name)}",
            ("Revision", revision));
        using var response = await SendWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, modelId, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = HubJson.UnwrapData(document);

        var files = await GetModelFilesAsync(modelId, revision, true, cancellationToken).ConfigureAwait(false);
        return new HubModelInfo(
            modelId,
            requestedRevision,
            revision,
            HubJson.GetString(data, "Description"),
            HubJson.GetString(data, "License"),
            HubJson.GetStringList(data, "Tasks"),
            HubJson.GetStringList(data, "Tags"),
            files);
    }

    public async Task<IReadOnlyList<ModelFileInfo>> GetModelFilesAsync(
        ModelId modelId,
        string? revision = null,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        revision = ResolveRevision(revision);
        var uri = BuildUri(
            $"api/v1/models/{Escape(modelId.Owner)}/{Escape(modelId.Name)}/repo/files",
            ("Revision", revision),
            ("Recursive", recursive ? "True" : "False"));
        using var response = await SendWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, modelId, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = HubJson.UnwrapData(document);
        if (HubJson.TryGet(data, "Files", out var filesElement))
        {
            data = filesElement;
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new ModelScopeException(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid ModelScope file inventory.");
        }

        var files = new List<ModelFileInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var path = HubJson.GetString(item, "Path") ?? HubJson.GetString(item, "Name");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            files.Add(new ModelFileInfo(
                path,
                HubJson.GetInt64(item, "Size"),
                HubJson.GetString(item, "Sha256"),
                HubJson.GetString(item, "Revision"),
                HubJson.GetString(item, "Type") ?? "blob",
                ParseTimestamp(item)));
        }

        return files;
    }

    public async Task<string> DownloadFileAsync(
        FileDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = SafePath.CombineUnderRoot(Path.GetTempPath(), request.FilePath);

        var revision = await ResolveModelRevisionAsync(request.ModelId, request.Revision, cancellationToken).ConfigureAwait(false);
        var destination = Path.GetFullPath(request.DestinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.ModelId}\n{revision}\n{request.FilePath}")));
        var temporary = destination + ".incomplete." + identity;

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadAttemptAsync(request, revision, temporary, progress, cancellationToken).ConfigureAwait(false);
                await ValidateDownloadedFileAsync(
                    temporary,
                    request.ExpectedSize,
                    request.ExpectedSha256,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, true);
                return destination;
            }
            catch (Exception exception) when (
                attempt < _options.MaxRetries &&
                exception is HttpRequestException or IOException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new UnreachableException();
    }

    public async Task<ModelSnapshot> DownloadSnapshotAsync(
        SnapshotDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var revision = ResolveRevision(request.Revision);
        var cacheDirectory = Path.GetFullPath(_options.CacheDirectory);
        var requestedRevision = revision;
        var referencePath = request.LocalDirectory is null && !IsCommitHash(revision)
            ? Path.Combine(cacheDirectory, "refs", Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{request.ModelId}\n{revision}"))) + ".ref") : null;
        if (referencePath is not null && !request.LocalFilesOnly)
            Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        // Serialize publication of one named reference, including its resolution and completed snapshot.
        await using var referenceLock = referencePath is not null && (!request.LocalFilesOnly || File.Exists(referencePath))
            ? await AcquireFileLockAsync(referencePath + ".lock", cancellationToken).ConfigureAwait(false) : null;
        if (!request.LocalFilesOnly)
            revision = await ResolveModelRevisionAsync(request.ModelId, requestedRevision, cancellationToken).ConfigureAwait(false);
        else if (referencePath is not null && File.Exists(referencePath))
            revision = await ReadCachedReferenceAsync(referencePath, cancellationToken).ConfigureAwait(false);
        else if (IsCommitHash(revision)) revision = revision.ToLowerInvariant();
        var snapshotRoot = request.LocalDirectory is null
            ? Path.Combine(
                cacheDirectory,
                "snapshots",
                SafePath.SanitizeSegment(request.ModelId.Owner),
                SafePath.SanitizeSegment(request.ModelId.Name),
                SafePath.SanitizeSegment(revision))
            : Path.GetFullPath(request.LocalDirectory);
        var manifestPath = Path.Combine(snapshotRoot, ".modelscope-net-manifest.json");

        if (request.LocalFilesOnly)
        {
            if (!File.Exists(manifestPath))
            {
                throw new ModelScopeException(ModelScopeErrorCode.ModelNotFound, "The requested snapshot is not available in the local cache.");
            }

            await using var offlineLock = await AcquireFileLockAsync(
                Path.Combine(snapshotRoot, ".modelscope-net.lock"), cancellationToken).ConfigureAwait(false);
            var cachedManifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (cachedManifest.ModelId != request.ModelId)
                throw new ModelScopeException(ModelScopeErrorCode.ModelNotFound, "The local snapshot belongs to a different model.");
            if (!string.Equals(revision, cachedManifest.RequestedRevision, StringComparison.Ordinal) &&
                !string.Equals(revision, cachedManifest.ResolvedRevision, StringComparison.Ordinal))
                throw new ModelScopeException(ModelScopeErrorCode.RevisionNotFound, "The requested revision is not present in this local snapshot.");
            var cachedFiles = new List<ModelFileInfo>(cachedManifest.Files.Count);
            foreach (var file in cachedManifest.Files)
            {
                var snapshotPath = SafePath.CombineUnderRoot(snapshotRoot, file.Path);
                if (!File.Exists(snapshotPath))
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.DownloadIntegrityFailed,
                        $"Cached snapshot file '{file.Path}' is missing.");
                }

                await ValidateDownloadedFileAsync(
                    snapshotPath,
                    file.Size > 0 ? file.Size : null,
                    file.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!IsIncluded(file.Path, request.AllowPatterns, request.IgnorePatterns)) continue;
                cachedFiles.Add(new ModelFileInfo(
                    file.Path,
                    file.Size,
                    file.Sha256,
                    cachedManifest.ResolvedRevision,
                    "blob"));
            }

            return new ModelSnapshot(
                request.ModelId,
                cachedManifest.ResolvedRevision,
                snapshotRoot,
                manifestPath,
                cachedFiles);
        }

        var allFiles = await GetModelFilesAsync(request.ModelId, revision, true, cancellationToken).ConfigureAwait(false);
        var files = allFiles.Where(file => file.IsFile && IsIncluded(file.Path, request.AllowPatterns, request.IgnorePatterns)).ToArray();
        Directory.CreateDirectory(snapshotRoot);
        await using var snapshotLock = await AcquireFileLockAsync(
            Path.Combine(snapshotRoot, ".modelscope-net.lock"),
            cancellationToken).ConfigureAwait(false);
        if (request.LocalDirectory is not null && File.Exists(manifestPath))
        {
            var prior = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (prior.ModelId != request.ModelId || !string.Equals(prior.ResolvedRevision, revision, StringComparison.Ordinal))
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest,
                    "The local directory contains a different model or Commit. Choose a new directory to preserve that snapshot.");
        }
        var blobRoot = Path.Combine(cacheDirectory, "blobs");
        Directory.CreateDirectory(blobRoot);

        var completed = 0;
        using var gate = new SemaphoreSlim(request.MaxWorkers ?? _options.MaxConcurrentDownloads);
        var snapshotFiles = new SnapshotFile[files.Length];
        var tasks = files.Select(async (file, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var blobKey = GetBlobKey(request.ModelId, revision, file);
                var blobPath = Path.Combine(blobRoot, blobKey[..2], blobKey);
                Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                await using var blobLock = await AcquireFileLockAsync(
                    blobPath + ".lock",
                    cancellationToken).ConfigureAwait(false);
                if (!await IsReusableBlobAsync(blobPath, file, cancellationToken).ConfigureAwait(false))
                {
                    await DownloadFileAsync(
                        new FileDownloadRequest(
                            request.ModelId,
                            file.Path,
                            blobPath,
                            revision,
                            file.Size > 0 ? file.Size : null,
                            file.Sha256),
                        new Progress<DownloadProgress>(value =>
                            progress?.Report(value with
                            {
                                CompletedFiles = Volatile.Read(ref completed),
                                TotalFiles = files.Length,
                            })),
                        cancellationToken).ConfigureAwait(false);
                }

                var snapshotPath = SafePath.CombineUnderRoot(snapshotRoot, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                MaterializeBlob(blobPath, snapshotPath);
                snapshotFiles[index] = new SnapshotFile(file.Path, file.Size, file.Sha256, blobPath);
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress(file.Path, file.Size, file.Size, count, files.Length));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var downloadedFiles = snapshotFiles;
        var createdAt = DateTimeOffset.UtcNow;
        IReadOnlyList<SnapshotFile> manifestFiles = downloadedFiles;
        if (IsPartialSnapshotRequest(request))
        {
            var existing = await TryReadCompatibleManifestAsync(
                manifestPath,
                request.ModelId,
                revision,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                createdAt = existing.CreatedAt;
                manifestFiles = await MergeSnapshotFilesAsync(
                    existing.Files,
                    downloadedFiles,
                    snapshotRoot,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var manifest = new SnapshotManifest(
            request.ModelId,
            requestedRevision,
            revision,
            createdAt,
            manifestFiles);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        if (referencePath is not null)
            await WriteCachedReferenceAsync(referencePath, revision, cancellationToken).ConfigureAwait(false);
        return new ModelSnapshot(
            request.ModelId,
            manifest.ResolvedRevision,
            snapshotRoot,
            manifestPath,
            manifest.Files.Select(file => new ModelFileInfo(
                file.Path,
                file.Size,
                file.Sha256,
                manifest.ResolvedRevision,
                "blob")).ToArray());
    }

    private static async Task<string> ReadCachedReferenceAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[41];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        var commit = Encoding.ASCII.GetString(bytes, 0, count);
        if (!IsCommitHash(commit))
            throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "Invalid cached model reference.");
        return commit.ToLowerInvariant();
    }

    private static async Task WriteCachedReferenceAsync(string path, string commit, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, commit, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task DownloadAttemptAsync(
        FileDownloadRequest request,
        string revision,
        string temporary,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        var uri = BuildUri(
            $"api/v1/models/{Escape(request.ModelId.Owner)}/{Escape(request.ModelId.Name)}/repo",
            ("Revision", revision),
            ("FilePath", request.FilePath));
        using var message = CreateRequest(HttpMethod.Get, uri);
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, request.ModelId, cancellationToken).ConfigureAwait(false);

        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            offset = 0;
            File.Delete(temporary);
        }

        var total = request.ExpectedSize ??
            (response.Content.Headers.ContentLength.HasValue
                ? response.Content.Headers.ContentLength.Value + offset
                : null);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            temporary,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        var received = offset;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(new DownloadProgress(request.FilePath, received, total));
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                if (attempt < _options.MaxRetries && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception exception) when (
                attempt < _options.MaxRetries &&
                exception is HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("ModelScope.Net/0.1");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }

        return request;
    }

    private Uri BuildUri(string relativePath, params (string Name, string? Value)[] query)
    {
        var builder = new StringBuilder();
        builder.Append(new Uri(_options.Endpoint, relativePath).AbsoluteUri);
        var separator = '?';
        foreach (var (name, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator);
            separator = '&';
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return new Uri(builder.ToString());
    }

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        ModelId modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelScopeErrorCode.AuthenticationRequired,
            HttpStatusCode.NotFound => ModelScopeErrorCode.ModelNotFound,
            HttpStatusCode.TooManyRequests => ModelScopeErrorCode.RemoteApiRateLimited,
            _ => ModelScopeErrorCode.RemoteApiUnavailable,
        };
        throw new ModelScopeException(
            code,
            $"ModelScope request failed with HTTP {(int)response.StatusCode}.",
            IsTransient(response.StatusCode));
    }

    private static async Task ValidateDownloadedFileAsync(
        string path,
        long? expectedSize,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actualSize = stream.Length;
        if (expectedSize.HasValue && actualSize != expectedSize.Value)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.DownloadIntegrityFailed,
                $"Downloaded file size {actualSize} does not match expected size {expectedSize.Value}.",
                true);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.DownloadIntegrityFailed,
                    "Downloaded file SHA-256 does not match repository metadata.",
                    true);
            }
        }
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element)
    {
        if (!HubJson.TryGet(element, "CommittedDate", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsPartialSnapshotRequest(SnapshotDownloadRequest request) =>
        request.AllowPatterns is { Count: > 0 } ||
        request.IgnorePatterns is { Count: > 0 };

    private static async Task<SnapshotManifest?> TryReadCompatibleManifestAsync(
        string manifestPath,
        ModelId modelId,
        string revision,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var existing = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            return existing.ModelId.Equals(modelId) &&
                string.Equals(existing.ResolvedRevision, revision, StringComparison.Ordinal)
                ? existing
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or ModelScopeException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<SnapshotFile>> MergeSnapshotFilesAsync(
        IReadOnlyList<SnapshotFile> existing,
        IReadOnlyList<SnapshotFile> downloaded,
        string snapshotRoot,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        foreach (var file in existing)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || merged.ContainsKey(file.Path))
            {
                continue;
            }

            string snapshotPath;
            try
            {
                snapshotPath = SafePath.CombineUnderRoot(snapshotRoot, file.Path);
            }
            catch (ModelScopeException)
            {
                continue;
            }

            if (!File.Exists(snapshotPath))
            {
                continue;
            }

            try
            {
                await ValidateDownloadedFileAsync(
                    snapshotPath,
                    file.Size > 0 ? file.Size : null,
                    file.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ModelScopeException)
            {
                continue;
            }

            merged[file.Path] = file;
        }

        foreach (var file in downloaded)
        {
            merged[file.Path] = file;
        }

        return merged.Values
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsIncluded(
        string path,
        IReadOnlyList<string>? allowPatterns,
        IReadOnlyList<string>? ignorePatterns)
    {
        var allowed = allowPatterns is null ||
            allowPatterns.Count == 0 ||
            allowPatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path));
        var ignored = ignorePatterns is not null &&
            ignorePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path));
        return allowed && !ignored;
    }

    private static string GetBlobKey(ModelId modelId, string revision, ModelFileInfo file)
    {
        if (!string.IsNullOrWhiteSpace(file.Sha256) &&
            file.Sha256.Length == 64 &&
            file.Sha256.All(Uri.IsHexDigit))
        {
            return file.Sha256.ToLowerInvariant();
        }

        var value = $"{modelId}\n{revision}\n{file.Path}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool IsCommitHash(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static async Task<bool> IsReusableBlobAsync(
        string path,
        ModelFileInfo file,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || (file.Size > 0 && new FileInfo(path).Length != file.Size))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            return true;
        }

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FileStream> AcquireFileLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (started.Elapsed >= _options.RequestTimeout)
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.RemoteApiUnavailable,
                        $"Timed out waiting for cache lock '{Path.GetFileName(path)}'.",
                        true);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void MaterializeBlob(string blobPath, string snapshotPath)
    {
        var snapshotInfo = new FileInfo(snapshotPath);
        if (snapshotInfo.Exists || snapshotInfo.LinkTarget is not null)
        {
            File.Delete(snapshotPath);
        }

        try
        {
            File.CreateSymbolicLink(snapshotPath, blobPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Copy(blobPath, snapshotPath, true);
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = $"{path}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, HubJson.Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<SnapshotManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, HubJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "Cached manifest is invalid.");
    }

    private string ResolveRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? _options.DefaultRevision : revision;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout ||
        (int)statusCode >= 500;

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt), 8_000));

    private static string Limit(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
