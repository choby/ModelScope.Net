using System.Text.Json;

namespace ModelScope.Net.Runtime.Tests;

public sealed class ReleaseManifestCertificationTests
{
    [Fact]
    public void ManifestContainsFiveToTenFixedRevisionCertifiedModelsAndRequiredTasks()
    {
        var models = ReadModels(Path.Combine(AppContext.BaseDirectory, "release-manifest-v1.yaml"));
        var certified = models
            .Where(model => model.Status.StartsWith("certified-preview", StringComparison.Ordinal))
            .ToArray();

        Assert.InRange(certified.Length, 5, 10);
        Assert.All(certified, model =>
        {
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            Assert.False(string.IsNullOrWhiteSpace(model.Scope));
            Assert.DoesNotContain("pending", model.Status, StringComparison.OrdinalIgnoreCase);
            AssertCertificationReport(model);
        });
        Assert.Contains(certified, model => model.Task == "sentence-embedding");
        Assert.Contains(certified, model => model.Task == "text-classification");
        Assert.Contains(certified, model => model.Task == "text-generation");
        Assert.Contains(certified, model =>
            model.Task is "image-classification" or "object-detection");
    }

    private static void AssertCertificationReport(ReleaseModel model)
    {
        Assert.StartsWith("tests/compatibility/", model.CertificationReport, StringComparison.Ordinal);
        var outputRelativePath = model.CertificationReport["tests/".Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(AppContext.BaseDirectory, outputRelativePath);
        Assert.True(File.Exists(path), $"Certification report '{model.CertificationReport}' was not copied to the test output.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var report = document.RootElement;
        Assert.Equal(model.ModelId, report.GetProperty("modelId").GetString());
        Assert.Equal(model.Revision, report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
    }

    private static IReadOnlyList<ReleaseModel> ReadModels(string path)
    {
        var models = new List<ReleaseModel>();
        ReleaseModelBuilder? current = null;
        var inModels = false;
        foreach (var raw in File.ReadLines(path))
        {
            if (raw == "models:")
            {
                inModels = true;
                continue;
            }
            if (!inModels) continue;
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0])) break;

            if (raw.StartsWith("  - modelId: ", StringComparison.Ordinal))
            {
                if (current is not null) models.Add(current.Build());
                current = new ReleaseModelBuilder { ModelId = Value(raw) };
                continue;
            }
            if (current is null || !raw.StartsWith("    ", StringComparison.Ordinal) ||
                raw.StartsWith("      ", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = raw.IndexOf(':', 4);
            if (separator < 0) continue;
            var key = raw[4..separator];
            var value = raw[(separator + 1)..].Trim();
            switch (key)
            {
                case "revision": current.Revision = value; break;
                case "task": current.Task = value; break;
                case "scope": current.Scope = value; break;
                case "certificationReport": current.CertificationReport = value; break;
                case "status": current.Status = value; break;
            }
        }
        if (current is not null) models.Add(current.Build());
        return models;
    }

    private static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim();

    private sealed class ReleaseModelBuilder
    {
        public string ModelId { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string CertificationReport { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public ReleaseModel Build()
        {
            Assert.False(string.IsNullOrWhiteSpace(ModelId));
            Assert.False(string.IsNullOrWhiteSpace(Revision));
            Assert.False(string.IsNullOrWhiteSpace(Task));
            Assert.False(string.IsNullOrWhiteSpace(Status));
            return new ReleaseModel(ModelId, Revision, Task, Scope, CertificationReport, Status);
        }
    }

    private sealed record ReleaseModel(
        string ModelId,
        string Revision,
        string Task,
        string Scope,
        string CertificationReport,
        string Status);
}
