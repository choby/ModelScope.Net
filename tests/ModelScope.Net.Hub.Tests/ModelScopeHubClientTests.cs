using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Hub;

namespace ModelScope.Net.Hub.Tests;

public sealed class ModelScopeHubClientTests
{
    [Theory]
    [InlineData(401, ModelScopeErrorCode.AuthenticationRequired)]
    [InlineData(403, ModelScopeErrorCode.AuthenticationRequired)]
    [InlineData(404, ModelScopeErrorCode.ModelNotFound)]
    [InlineData(429, ModelScopeErrorCode.RemoteApiRateLimited)]
    [InlineData(503, ModelScopeErrorCode.RemoteApiUnavailable)]
    public async Task HttpAndEnvelopeFailuresMapWithoutEchoingSecrets(int status, ModelScopeErrorCode expected)
    {
        const string sensitive = "Bearer fixture-secret private-model-name https://private.invalid/?token=secret";
        foreach (var envelope in new[] { false, true })
        {
            using var http = new HttpClient(new StubHandler(_ => envelope
                ? Json(JsonSerializer.Serialize(new { Code = status, Success = true, Message = sensitive, Data = new { Files = Array.Empty<object>() } }))
                : new((HttpStatusCode)status) { Content = new StringContent(sensitive) }));
            var client = new ModelScopeHubClient(http, new() { MaxRetries = 0 });
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => client.GetModelFilesAsync(ModelId.Parse("private/model")));
            Assert.Equal(expected, error.Code);
            Assert.Equal(status == 429 || status >= 500, error.IsRetryable);
            Assert.DoesNotContain("secret", error.Message);
            Assert.DoesNotContain("private", error.Message);
        }
    }

    [Theory]
    [InlineData("{\"Success\":false,\"Message\":\"fixture-secret\",\"Data\":{\"Files\":[]}}")]
    [InlineData("{\"Success\":\"false\",\"Data\":{\"Files\":[]}}")]
    [InlineData("{\"Code\":\"invalid\",\"Data\":{\"Files\":[]}}")]
    [InlineData("{\"Code\":200,\"Data\":null}")]
    [InlineData("{\"Code\":200,\"Data\":{\"Files\":{}}}")]
    public async Task InvalidOrFailedInventoryNeverBecomesEmptySuccess(string json)
    {
        var client = CreateClient(new StubHandler(_ => Json(json)));
        var error = await Assert.ThrowsAsync<ModelScopeException>(() => client.GetModelFilesAsync(ModelId.Parse("owner/model")));
        Assert.Equal(ModelScopeErrorCode.RemoteApiUnavailable, error.Code);
        Assert.DoesNotContain("fixture-secret", error.Message);
    }

    [Fact]
    public async Task BranchCacheKeepsOldCommitAndPublishesOnlySuccessfulGeneration()
    {
        var directory = CreateTempDirectory();
        try
        {
            var head = new string('a', 40);
            var failDownload = false;
            var client = CreateClient(new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal))
                    return GitRefs(head + " refs/heads/master\n");
                var content = Encoding.UTF8.GetBytes(head[0] == 'a' ? "one" : "two");
                if (request.RequestUri.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal))
                    return Json(JsonSerializer.Serialize(new { Data = new { Files = new[] {
                        new { Path = "config.json", Size = 3, Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) } } } }));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(failDownload ? "bad"u8.ToArray() : content) };
            }), cacheRoot: directory);
            var request = new SnapshotDownloadRequest(ModelId.Parse("owner/model"), "master");
            var first = await client.DownloadSnapshotAsync(request);
            Assert.Equal(head, Path.GetFileName(first.Directory));
            head = new string('b', 40);
            failDownload = true;
            await Assert.ThrowsAsync<ModelScopeException>(() => client.DownloadSnapshotAsync(request));
            var offline = CreateClient(new StubHandler(_ => throw new InvalidOperationException("Offline accessed network")), cacheRoot: directory);
            Assert.Equal(first.Directory, (await offline.DownloadSnapshotAsync(request with { LocalFilesOnly = true })).Directory);
            failDownload = false;
            var second = await client.DownloadSnapshotAsync(request);
            Assert.NotEqual(first.Directory, second.Directory);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(first.Directory, "config.json")));
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(second.Directory, "config.json")));
            Assert.Equal(second.Directory, (await offline.DownloadSnapshotAsync(request with { LocalFilesOnly = true })).Directory);
            Assert.Equal(first.Directory, (await offline.DownloadSnapshotAsync(request with { Revision = new string('a', 40), LocalFilesOnly = true })).Directory);
            var pointer = Assert.Single(Directory.GetFiles(Path.Combine(directory, "refs"), "*.ref"));
            await File.WriteAllTextAsync(pointer, "../../unexpected");
            var corrupted = await Assert.ThrowsAsync<ModelScopeException>(() => offline.DownloadSnapshotAsync(request with { LocalFilesOnly = true }));
            Assert.Equal(ModelScopeErrorCode.DownloadIntegrityFailed, corrupted.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExplicitDirectoryCannotOverwriteAnotherCommit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var prior = new SnapshotManifest(ModelId.Parse("owner/model"), "master", new string('a', 40), DateTimeOffset.UtcNow, []);
            var manifestPath = Path.Combine(directory, ".modelscope-net-manifest.json");
            var original = JsonSerializer.Serialize(prior, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(manifestPath, original);
            var client = CreateClient(new StubHandler(_ => Json("""{"Data":{"Files":[]}}""")));
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => client.DownloadSnapshotAsync(
                new(ModelId.Parse("owner/model"), new string('b', 40), directory)));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(manifestPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ModelDetailsAndInventoryShareOneResolvedCommit()
    {
        var commit = new string('b', 40);
        var resolutions = 0;
        var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal))
            {
                resolutions++;
                return GitRefs(commit + " refs/heads/master\n");
            }
            Assert.Contains("Revision=" + commit, request.RequestUri.Query);
            return request.RequestUri.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal)
                ? Json("""{"Data":{"Files":[]}}""") : Json("""{"Data":{"Description":"fixture"}}""");
        }));
        var model = await client.GetModelAsync(ModelId.Parse("owner/model"), "master");
        Assert.Equal("master", model.Revision);
        Assert.Equal(commit, model.Commit);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task SingleFileRetriesStayPinnedAndDoNotResumeAnotherCommitOrLegacyPartial()
    {
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "config.json");
            var oldPartial = destination + ".incomplete." + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"owner/model\n{new string('a', 40)}\nconfig.json")));
            await File.WriteAllTextAsync(oldPartial, "old bytes");
            await File.WriteAllTextAsync(destination + ".incomplete", "legacy bytes");
            var commit = new string('b', 40);
            var resolutions = 0;
            var downloads = 0;
            var client = CreateClient(new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal))
                {
                    resolutions++;
                    return GitRefs((resolutions == 1 ? commit : new string('c', 40)) + " refs/heads/master\n");
                }
                Assert.Contains("Revision=" + commit, request.RequestUri.Query);
                Assert.Null(request.Headers.Range);
                if (++downloads == 1) throw new HttpRequestException("Injected transient failure");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent("new"u8.ToArray()) };
            }));
            await client.DownloadFileAsync(new(ModelId.Parse("owner/model"), "config.json", destination, "master", 3));
            Assert.Equal("new", await File.ReadAllTextAsync(destination));
            Assert.Equal(1, resolutions);
            Assert.Equal(2, downloads);
            Assert.Equal("old bytes", await File.ReadAllTextAsync(oldPartial));
            Assert.Equal("legacy bytes", await File.ReadAllTextAsync(destination + ".incomplete"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static HttpResponseMessage GitRefs(params string[] refs)
    {
        static string Packet(string data) => (Encoding.UTF8.GetByteCount(data) + 4).ToString("x4") + data;
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(Packet("# service=git-upload-pack\n") + "0000" +
                string.Concat(refs.Select(Packet)) + "0000", Encoding.UTF8, "application/x-git-upload-pack-advertisement")
        };
    }

    [Theory]
    [InlineData("master")]
    [InlineData("refs/heads/master")]
    [InlineData("v1")]
    public async Task ResolveReferenceUsesAdvertisedBranchOrPeeledTag(string reference)
    {
        var commit = new string('b', 40);
        var client = CreateClient(new StubHandler(request =>
        {
            Assert.EndsWith(".git/info/refs", request.RequestUri!.AbsolutePath);
            Assert.Equal("git/ModelScope.Net", request.Headers.UserAgent.ToString());
            return GitRefs(commit + " HEAD\0symref=HEAD:refs/heads/master\n", commit + " refs/heads/master\n",
                new string('c', 40) + " refs/tags/v1\n", commit + " refs/tags/v1^{}\n");
        }));
        Assert.Equal(commit, await client.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), reference));
    }

    [Fact]
    public async Task ResolveReferenceRejectsAmbiguityAndDoesNotTreatShortHashAsFixedCommit()
    {
        var client = CreateClient(new StubHandler(_ => GitRefs(new string('b', 40) + " refs/heads/same\n",
            new string('c', 40) + " refs/tags/same\n")));
        var ambiguous = await Assert.ThrowsAsync<ModelScopeException>(() => client.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), "same"));
        Assert.Equal(ModelScopeErrorCode.InvalidRequest, ambiguous.Code);
        var missing = await Assert.ThrowsAsync<ModelScopeException>(() => client.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), "abcdef1"));
        Assert.Equal(ModelScopeErrorCode.RevisionNotFound, missing.Code);
        Assert.Equal(new string('c', 40), await client.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), "refs/tags/same"));
    }

    [Fact]
    public async Task ResolveReferenceRejectsTruncatedAdvertisementAndPreservesFullCommitWithoutNetwork()
    {
        var client = CreateClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("ffffbad", Encoding.UTF8, "application/x-git-upload-pack-advertisement") }));
        await Assert.ThrowsAsync<ModelScopeException>(() => client.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), "master"));
        var offline = CreateClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected network.")));
        Assert.Equal(new string('a', 40), await offline.ResolveModelRevisionAsync(ModelId.Parse("owner/model"), new string('A', 40)));
    }

    [Fact]
    public async Task SnapshotResolvesOnceAndPinsFileListAndDownloadToSameCommit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var commit = new string('b', 40);
            var resolutions = 0;
            var bytes = "pinned"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var client = CreateClient(new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal))
                {
                    resolutions++;
                    return GitRefs(commit + " refs/heads/master\n");
                }
                Assert.Contains("Revision=" + commit, request.RequestUri.Query);
                if (request.RequestUri.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal))
                    return Json(JsonSerializer.Serialize(new { Data = new { Files = new[] { new { Path = "config.json", Size = 6, Sha256 = hash } } } }));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }), cacheRoot: Path.Combine(directory, "cache"));
            var result = await client.DownloadSnapshotAsync(new(ModelId.Parse("owner/model"), "master", Path.Combine(directory, "snapshot")));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
            Assert.Equal("master", manifest.RootElement.GetProperty("requestedRevision").GetString());
            Assert.Equal(commit, manifest.RootElement.GetProperty("resolvedRevision").GetString());
            Assert.Equal(1, resolutions);
            Assert.Equal("pinned", await File.ReadAllTextAsync(Path.Combine(result.Directory, "config.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task RevisionsPreserveReferenceKindsAndNormalizeSecondsAndMilliseconds()
    {
        var client = CreateClient(new StubHandler(request =>
        {
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            Assert.EndsWith("/owner/model/revisions", request.RequestUri!.AbsolutePath);
            return Json("""{"Data":{"RevisionMap":{"Branches":[{"Revision":"master","CreatedAt":1726204881}],"Tags":[{"Revision":"master","CreatedAt":"1726204881000"},{"Revision":"v1","CreatedAt":null}]}}} """);
        }), token: "secret");
        var revisions = await client.GetModelRevisionsAsync(ModelId.Parse("owner/model"));
        Assert.Equal(3, revisions.Count);
        Assert.Equal(HubRevisionKind.Branch, revisions[0].Kind);
        Assert.Equal(HubRevisionKind.Tag, revisions[1].Kind);
        Assert.Equal(revisions[0].CreatedAt, revisions[1].CreatedAt);
        Assert.Null(revisions[2].CreatedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Data\":{\"RevisionMap\":{\"Tags\":{}}}}")]
    [InlineData("{\"Data\":{\"RevisionMap\":{\"Branches\":[{\"Revision\":\"x\"},{\"Revision\":\"x\"}]}}}")]
    public async Task RevisionsRejectMalformedOrDuplicateLists(string json)
    {
        var client = CreateClient(new StubHandler(_ => Json(json)));
        var error = await Assert.ThrowsAsync<ModelScopeException>(() => client.GetModelRevisionsAsync(ModelId.Parse("owner/model")));
        Assert.Equal(ModelScopeErrorCode.RemoteApiUnavailable, error.Code);
    }

    [Fact]
    public async Task RevisionsAcceptEmptyRepositoryAndNullCollections()
    {
        var client = CreateClient(new StubHandler(_ => Json("""{"Data":{"RevisionMap":{"Branches":[],"Tags":null}}} """)));
        Assert.Empty(await client.GetModelRevisionsAsync(ModelId.Parse("owner/model")));
    }

    [Fact]
    public async Task OfflineSnapshot_RejectsWrongModelOrRevision_AndHonorsFiltersWithoutNetwork()
    {
        var directory = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("fixture");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(Path.Combine(directory, "config.json"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, "weights.bin"), bytes);
            var revision = new string('a', 40);
            var manifest = new SnapshotManifest(ModelId.Parse("owner/model"), "master", revision, DateTimeOffset.UtcNow,
                [new("config.json", bytes.Length, hash, ""), new("weights.bin", bytes.Length, hash, "")]);
            await File.WriteAllTextAsync(Path.Combine(directory, ".modelscope-net-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var client = CreateClient(new StubHandler(_ => throw new InvalidOperationException("Offline mode accessed the network.")));
            var request = new SnapshotDownloadRequest(ModelId.Parse("owner/model"),
                Revision: revision, LocalDirectory: directory, LocalFilesOnly: true, AllowPatterns: ["*.json"]);
            Assert.Equal("config.json", Assert.Single((await client.DownloadSnapshotAsync(request)).Files).Path);
            var wrongModel = await Assert.ThrowsAsync<ModelScopeException>(() =>
                client.DownloadSnapshotAsync(request with { ModelId = ModelId.Parse("owner/other") }));
            Assert.Equal(ModelScopeErrorCode.ModelNotFound, wrongModel.Code);
            var wrongRevision = await Assert.ThrowsAsync<ModelScopeException>(() =>
                client.DownloadSnapshotAsync(request with { Revision = new string('b', 40) }));
            Assert.Equal(ModelScopeErrorCode.RevisionNotFound, wrongRevision.Code);
            Assert.Single((await client.DownloadSnapshotAsync(request with { Revision = "master" })).Files);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task GetModelFilesAsync_ParsesEnvelopeAndSendsToken()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            Assert.Contains("/api/v1/models/Qwen/Demo/repo/files", request.RequestUri?.AbsolutePath);
            return Json("""
                {
                  "Data": {
                    "Files": [
                      { "Name": "config.json", "Path": "config.json", "Size": 12, "Sha256": "abc" }
                    ]
                  }
                }
                """);
        });
        var client = CreateClient(handler, token: "secret");

        var files = await client.GetModelFilesAsync(ModelId.Parse("Qwen/Demo"));

        var file = Assert.Single(files);
        Assert.Equal("config.json", file.Path);
        Assert.Equal(12, file.Size);
        Assert.Equal("abc", file.Sha256);
    }

    [Fact]
    public async Task DownloadFileAsync_ResumesPartialFileAndValidatesHash()
    {
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "weights.bin");
            var partial = destination + ".incomplete." + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"owner/model\n{new string('a', 40)}\nweights.bin")));
            await File.WriteAllTextAsync(partial, "hello");
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello world"))).ToLowerInvariant();
            var handler = new StubHandler(request =>
            {
                Assert.Equal(new RangeHeaderValue(5, null).ToString(), request.Headers.Range?.ToString());
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(" world"))
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 10, 11);
                return response;
            });
            var client = CreateClient(handler);
            var reports = new List<DownloadProgress>();

            var result = await client.DownloadFileAsync(new FileDownloadRequest(
                ModelId.Parse("owner/model"),
                "weights.bin",
                destination,
                ExpectedSize: 11,
                ExpectedSha256: expected),
                new InlineProgress<DownloadProgress>(reports.Add));

            Assert.Equal(destination, result);
            Assert.Equal("hello world", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(partial));
            Assert.Contains(reports, report => report.BytesReceived == 11);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_HonorsPreCanceledToken()
    {
        var directory = CreateTempDirectory();
        try
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP request was expected."));
            var client = CreateClient(handler);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadFileAsync(
                new FileDownloadRequest(
                    ModelId.Parse("owner/model"),
                    "config.json",
                    Path.Combine(directory, "config.json")),
                cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetModelFilesAsync_MapsUnauthorizedResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized"),
        });
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ModelScopeException>(() =>
            client.GetModelFilesAsync(ModelId.Parse("owner/private-model")));

        Assert.Equal(ModelScopeErrorCode.AuthenticationRequired, error.Code);
    }

    [Fact]
    public async Task DownloadSnapshotAsync_RespectsMaxWorkers()
    {
        var directory = CreateTempDirectory();
        try
        {
            var inFlight = 0;
            var maximum = 0;
            var files = Enumerable.Range(0, 4)
                .Select(index =>
                {
                    var bytes = Encoding.UTF8.GetBytes(index.ToString());
                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    return (Path: $"file-{index}.txt", Bytes: bytes, Hash: hash);
                })
                .ToArray();
            var listing = JsonSerializer.Serialize(new
            {
                Data = new
                {
                    Files = files.Select(file => new
                    {
                        Name = file.Path,
                        Path = file.Path,
                        Size = file.Bytes.Length,
                        Sha256 = file.Hash,
                    }),
                },
            });
            var handler = new AsyncStubHandler(async request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal) == true)
                {
                    return Json(listing);
                }

                var current = Interlocked.Increment(ref inFlight);
                UpdateMaximum(ref maximum, current);
                try
                {
                    await Task.Delay(40);
                    var file = files.Single(item => request.RequestUri!.Query.Contains(item.Path, StringComparison.Ordinal));
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(file.Bytes),
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });
            var client = CreateClient(handler, cacheRoot: Path.Combine(directory, "cache"));

            var snapshot = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: Path.Combine(directory, "snapshot"),
                MaxWorkers: 2));

            Assert.Equal(4, snapshot.Files.Count);
            Assert.Equal(2, maximum);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadSnapshotAsync_AppliesAllowPatternsAndWritesManifest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var textBytes = Encoding.UTF8.GetBytes("demo");
            var hash = Convert.ToHexString(SHA256.HashData(textBytes)).ToLowerInvariant();
            var handler = new StubHandler(request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal) == true)
                {
                    return Json($$"""
                        { "Data": { "Files": [
                          { "Name": "weights.bin", "Path": "weights.bin", "Size": 2, "Sha256": "x" },
                          { "Name": "config.json", "Path": "config.json", "Size": {{textBytes.Length}}, "Sha256": "{{hash}}" }
                        ] } }
                        """);
                }

                Assert.Contains("FilePath=config.json", request.RequestUri?.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(textBytes)
                };
            });
            var absoluteCache = Path.Combine(directory, "cache");
            var relativeCache = Path.GetRelativePath(Environment.CurrentDirectory, absoluteCache);
            var client = CreateClient(handler, cacheRoot: relativeCache);

            var snapshot = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: Path.Combine(directory, "snapshots"),
                AllowPatterns: ["*.json"]));

            Assert.Single(snapshot.Files);
            Assert.Equal("config.json", snapshot.Files[0].Path);
            Assert.True(File.Exists(Path.Combine(snapshot.Directory, "config.json")));
            Assert.Equal("demo", await File.ReadAllTextAsync(Path.Combine(snapshot.Directory, "config.json")));
            Assert.True(File.Exists(snapshot.ManifestPath));
            Assert.False(File.Exists(Path.Combine(snapshot.Directory, "weights.bin")));

            var offline = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: Path.Combine(directory, "snapshots"),
                LocalFilesOnly: true));
            Assert.Single(offline.Files);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadSnapshotAsync_FilteredRedownloadKeepsCompleteManifest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configBytes = Encoding.UTF8.GetBytes("config");
            var weightBytes = Encoding.UTF8.GetBytes("weights");
            var configHash = Convert.ToHexString(SHA256.HashData(configBytes)).ToLowerInvariant();
            var weightHash = Convert.ToHexString(SHA256.HashData(weightBytes)).ToLowerInvariant();
            var listing = $$"""
                { "Data": { "Files": [
                  { "Name": "weights.bin", "Path": "weights.bin", "Size": {{weightBytes.Length}}, "Sha256": "{{weightHash}}" },
                  { "Name": "config.json", "Path": "config.json", "Size": {{configBytes.Length}}, "Sha256": "{{configHash}}" }
                ] } }
                """;
            var downloadedPaths = new List<string>();
            var handler = new StubHandler(request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/repo/files", StringComparison.Ordinal) == true)
                {
                    return Json(listing);
                }

                var query = request.RequestUri?.Query ?? string.Empty;
                downloadedPaths.Add(query);
                var bytes = query.Contains("FilePath=config.json", StringComparison.Ordinal)
                    ? configBytes
                    : weightBytes;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                };
            });
            var snapshotDirectory = Path.Combine(directory, "snapshots");
            var client = CreateClient(handler, cacheRoot: Path.Combine(directory, "cache"));

            var complete = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: snapshotDirectory));
            Assert.Equal(2, complete.Files.Count);
            var stored = JsonSerializer.Deserialize<SnapshotManifest>(
                await File.ReadAllTextAsync(complete.ManifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            Assert.Equal("owner/model", stored!.ModelId.ToString());
            Assert.Equal(2, stored.Files.Count);

            downloadedPaths.Clear();
            var filtered = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: snapshotDirectory,
                AllowPatterns: ["*.json"]));

            Assert.Equal(2, filtered.Files.Count);
            Assert.Contains(filtered.Files, file => file.Path == "config.json");
            Assert.Contains(filtered.Files, file => file.Path == "weights.bin");
            Assert.True(File.Exists(Path.Combine(snapshotDirectory, "weights.bin")));
            Assert.All(downloadedPaths, query => Assert.Contains("FilePath=config.json", query, StringComparison.Ordinal));

            var offline = await client.DownloadSnapshotAsync(new SnapshotDownloadRequest(
                ModelId.Parse("owner/model"),
                LocalDirectory: snapshotDirectory,
                LocalFilesOnly: true));
            Assert.Equal(2, offline.Files.Count);
            Assert.Equal("config", await File.ReadAllTextAsync(Path.Combine(snapshotDirectory, "config.json")));
            Assert.Equal("weights", await File.ReadAllTextAsync(Path.Combine(snapshotDirectory, "weights.bin")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelScopeHubClient CreateClient(
        HttpMessageHandler handler,
        string? token = null,
        string? cacheRoot = null)
    {
        return new ModelScopeHubClient(
            new HttpClient(handler),
            new ModelScopeHubOptions
            {
                Token = token,
                DefaultRevision = new string('a', 40),
                CacheDirectory = cacheRoot ?? Path.Combine(Path.GetTempPath(), "modelscope-net-tests")
            });
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var current = Volatile.Read(ref maximum);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }
}
