using System.Text.Json;

namespace ModelScope.Net.Runtime.Tests;

public sealed class PerformanceEvidenceTests
{
    [Fact]
    public async Task P308CpuReport_MeetsPublishedPreviewThresholds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "performance", "p3-08-cpu-macos-arm64.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal("technical-preview-macos-arm64-cpu", report.GetProperty("scope").GetString());
        Assert.Equal(2, report.GetProperty("methodology").GetProperty("repeatRuns").GetInt32());
        Assert.Equal(5, report.GetProperty("summary").GetProperty("certifiedModels").GetInt32());
        Assert.Equal(5, report.GetProperty("summary").GetProperty("passedModels").GetInt32());
        Assert.Equal(0, report.GetProperty("summary").GetProperty("failedModels").GetInt32());
        Assert.Equal(2, report.GetProperty("rawRuns").GetArrayLength());

        var models = report.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(5, models.Length);
        Assert.Contains(models, model => model.GetProperty("task").GetString() == "sentence-embedding");
        Assert.Contains(models, model => model.GetProperty("task").GetString() == "text-classification");
        Assert.Contains(models, model => model.GetProperty("task").GetString() == "image-classification");
        Assert.Contains(models, model => model.GetProperty("task").GetString() == "text-generation");

        Assert.All(models, model =>
        {
            Assert.Matches("^[0-9a-f]{40}$", model.GetProperty("revision").GetString()!);
            Assert.True(model.GetProperty("passed").GetBoolean());
            var threshold = model.GetProperty("threshold");
            Assert.True(
                model.GetProperty("loadMilliseconds").GetDouble() <=
                threshold.GetProperty("maximumLoadMilliseconds").GetDouble());
            Assert.True(
                model.GetProperty("peakWorkingSetMiB").GetDouble() <=
                threshold.GetProperty("maximumWorkingSetMiB").GetDouble());
            Assert.All(model.GetProperty("scenarios").EnumerateArray(), scenario =>
            {
                Assert.True(scenario.GetProperty("passed").GetBoolean());
                var scenarioThreshold = scenario.GetProperty("threshold");
                Assert.True(
                    scenario.GetProperty("p95Milliseconds").GetDouble() <=
                    scenarioThreshold.GetProperty("maximumP95Milliseconds").GetDouble());
                Assert.True(
                    scenario.GetProperty("throughput").GetDouble() >=
                    scenarioThreshold.GetProperty("minimumThroughput").GetDouble());
            });
        });
    }
}
