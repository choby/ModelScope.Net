using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Hub;

namespace ModelScope.Net.Hub.Tests;

public sealed class ModelScopeCacheInspectorTests
{
    [Fact]
    public async Task ScanAndVerifyAsync_ReportValidThenCorruptedSnapshot()
    {
        var directory = CreateTempDirectory();
        try
        {
            var cache = Path.Combine(directory, "cache");
            var snapshot = Path.Combine(cache, "snapshots", "owner", "model", "commit");
            var blobDirectory = Path.Combine(cache, "blobs", "aa");
            Directory.CreateDirectory(snapshot);
            Directory.CreateDirectory(blobDirectory);
            var bytes = Encoding.UTF8.GetBytes("demo");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var snapshotFile = Path.Combine(snapshot, "config.json");
            var blobFile = Path.Combine(blobDirectory, hash);
            await File.WriteAllBytesAsync(snapshotFile, bytes);
            await File.WriteAllBytesAsync(blobFile, bytes);
            await File.WriteAllTextAsync(Path.Combine(cache, "orphan.incomplete"), "partial");
            var manifest = new SnapshotManifest(
                ModelId.Parse("owner/model"),
                "commit",
                "commit",
                DateTimeOffset.UtcNow,
                [new SnapshotFile("config.json", bytes.Length, hash, blobFile)]);
            await File.WriteAllTextAsync(
                Path.Combine(snapshot, ".modelscope-net-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var inspector = new ModelScopeCacheInspector(cache);

            var summary = inspector.Scan();
            var valid = await inspector.VerifyAsync();

            Assert.Equal(1, summary.BlobCount);
            Assert.Equal(bytes.Length, summary.BlobBytes);
            Assert.Equal(1, summary.SnapshotCount);
            Assert.Equal(1, summary.IncompleteFileCount);
            Assert.True(valid.IsValid);

            await File.WriteAllTextAsync(snapshotFile, "evil");
            var invalid = await inspector.VerifyAsync();

            Assert.False(invalid.IsValid);
            Assert.Contains(invalid.Issues, issue => issue.Code == "HashMismatch");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
