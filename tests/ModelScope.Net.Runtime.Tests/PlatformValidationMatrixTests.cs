using System.Text.Json;

namespace ModelScope.Net.Runtime.Tests;

public sealed class PlatformValidationMatrixTests
{
    [Fact]
    public void ProviderCombinations_HaveAuditableIdentityEvidenceAndGaps()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment", "platform-validation-matrix.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var combinations = document.RootElement.GetProperty("providerCombinations").EnumerateArray().ToArray();

        Assert.NotEmpty(combinations);
        Assert.Equal(
            combinations.Length,
            combinations.Select(item => RequiredString(item, "id")).Distinct(StringComparer.Ordinal).Count());

        foreach (var combination in combinations)
        {
            RequiredString(combination, "os");
            RequiredString(combination, "architecture");
            RequiredString(combination, "status");
            RequiredString(combination, "evidenceLevel");
            Assert.True(combination.GetProperty("deviceId").GetInt32() >= 0);

            var status = combination.GetProperty("status").GetString()!;
            var evidence = combination.GetProperty("evidence").EnumerateArray().ToArray();
            var gaps = combination.GetProperty("blockingGaps").EnumerateArray().ToArray();
            Assert.All(evidence, item => Assert.False(string.IsNullOrWhiteSpace(item.GetString())));
            Assert.All(gaps, item => Assert.False(string.IsNullOrWhiteSpace(item.GetString())));

            if (status.StartsWith("certified", StringComparison.OrdinalIgnoreCase))
            {
                Assert.NotEmpty(evidence);
                Assert.Empty(gaps);
                RequiredString(combination, "executionProvider");
                RequiredString(combination, "nativePackage");
                RequiredString(combination, "hardware");
            }
            else if (status.StartsWith("approved-for-validation", StringComparison.OrdinalIgnoreCase))
            {
                RequiredString(combination, "executionProvider");
                RequiredString(combination, "nativePackage");
                RequiredString(combination, "hardware");
                Assert.NotEmpty(gaps);
            }
            else
            {
                Assert.NotEmpty(gaps);
            }
        }
    }

    [Fact]
    public void CurrentDraft_DoesNotClaimApprovalOrGpuCertification()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment", "platform-validation-matrix.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        Assert.StartsWith("draft-", RequiredString(root, "status"), StringComparison.Ordinal);
        var approvals = root.GetProperty("approvals");
        Assert.Equal("pending", approvals.GetProperty("technical").GetString());
        Assert.Equal("pending", approvals.GetProperty("sre").GetString());

        var gpu = root.GetProperty("providerCombinations").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "linux-x64-nvidia-onnx-accelerated-device0");
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("executionProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("hardware").ValueKind);
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("driver").ValueKind);
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("cuda").ValueKind);
        Assert.Equal("none", gpu.GetProperty("evidenceLevel").GetString());
        Assert.Empty(gpu.GetProperty("evidence").EnumerateArray());
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value!;
    }
}
