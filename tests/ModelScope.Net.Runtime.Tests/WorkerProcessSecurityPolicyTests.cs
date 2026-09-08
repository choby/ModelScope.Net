using System.Diagnostics;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Tests;

public sealed class WorkerProcessSecurityPolicyTests
{
    [Fact]
    public void ApplyEnvironment_UsesAllowlistAndConfiguredNonSensitiveValues()
    {
        const string secretName = "MODELSCOPE_NET_TEST_POLICY_TOKEN";
        var previous = Environment.GetEnvironmentVariable(secretName);
        Environment.SetEnvironmentVariable(secretName, "parent-secret");
        try
        {
            var startInfo = new ProcessStartInfo();
            WorkerProcessSecurityPolicy.ApplyEnvironment(
                startInfo,
                new Dictionary<string, string> { ["WORKER_MODE"] = "isolated" },
                new WorkerProcessSecurityOptions());

            Assert.False(startInfo.Environment.ContainsKey(secretName));
            Assert.Equal("isolated", startInfo.Environment["WORKER_MODE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
        }
    }

    [Fact]
    public void ApplyEnvironment_RejectsSensitiveConfiguredValuesByDefault()
    {
        var startInfo = new ProcessStartInfo();

        var error = Assert.Throws<ArgumentException>(() => WorkerProcessSecurityPolicy.ApplyEnvironment(
            startInfo,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "secret" },
            new WorkerProcessSecurityOptions()));

        Assert.Contains("classified as sensitive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeDiagnostic_RedactsCredentialsControlsAndLongLines()
    {
        var options = new WorkerProcessSecurityOptions { MaxDiagnosticCharacters = 128 };
        var token = string.Concat("ms-", "12345678-", "1234-", "1234-", "1234-", "123456789abc");
        var source = $"Authorization: Bearer abc123 token={token}\0 public " + new string('x', 300);

        var sanitized = WorkerProcessSecurityPolicy.SanitizeDiagnostic(source, options);

        Assert.DoesNotContain("abc123", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(token, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', sanitized);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length <= 129);
    }
}
