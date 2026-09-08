using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Tests;

public sealed class CompatibilityCatalogTests
{
    [Fact]
    public async Task LoadAndVerifyAsync_AcceptsSignedCatalogAndRejectsTampering()
    {
        var directory = CreateTempDirectory();
        try
        {
            var catalogPath = Path.Combine(directory, "catalog.json");
            var signaturePath = Path.Combine(directory, "catalog.sig");
            var catalog = new CompatibilityCatalog(
                1,
                "preview-1",
                DateTimeOffset.UtcNow,
                [new CompatibilityCatalogEntry(
                    "owner/model",
                    "186d8559ad54c32cf47dc3a8225f993742c507b8",
                    "text-generation",
                    "remote",
                    CapabilityStatus.Certified)]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                catalog,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(catalogPath, bytes);
            using var rsa = RSA.Create(2048);
            var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(signature));
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

            var loaded = await CompatibilityCatalogLoader.LoadAndVerifyAsync(
                catalogPath,
                signaturePath,
                publicKey);

            Assert.Equal("preview-1", loaded.CatalogVersion);
            Assert.Contains("owner/model", loaded.GetCertifiedModels("remote"));

            await File.AppendAllTextAsync(catalogPath, " ");
            var error = await Assert.ThrowsAsync<ModelScopeException>(() =>
                CompatibilityCatalogLoader.LoadAndVerifyAsync(catalogPath, signaturePath, publicKey));
            Assert.Equal(ModelScopeErrorCode.DownloadIntegrityFailed, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAndVerifyAsync_RejectsMovingBranchEvenWhenSignatureIsValid()
    {
        var directory = CreateTempDirectory();
        try
        {
            var catalogPath = Path.Combine(directory, "catalog.json");
            var signaturePath = Path.Combine(directory, "catalog.sig");
            var json = """
                {
                  "schemaVersion": 1,
                  "catalogVersion": "invalid",
                  "createdAt": "2026-09-01T00:00:00Z",
                  "models": [{
                    "modelId": "owner/model",
                    "revision": "master",
                    "task": "chat",
                    "runtime": "remote",
                    "status": "certified"
                  }]
                }
                """;
            var bytes = Encoding.UTF8.GetBytes(json);
            await File.WriteAllBytesAsync(catalogPath, bytes);
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(
                signaturePath,
                Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));

            await Assert.ThrowsAsync<ModelScopeException>(() => CompatibilityCatalogLoader.LoadAndVerifyAsync(
                catalogPath,
                signaturePath,
                rsa.ExportSubjectPublicKeyInfoPem()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
