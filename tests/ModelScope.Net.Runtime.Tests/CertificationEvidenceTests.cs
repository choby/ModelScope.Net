using System.Text.Json;

namespace ModelScope.Net.Runtime.Tests;

public sealed class CertificationEvidenceTests
{
    [Fact]
    public async Task AllMiniLmL6V2_ReportMeetsPublishedThresholds()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "all-minilm-l6-v2",
            "certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("unsloth/all-MiniLM-L6-v2", report.GetProperty("modelId").GetString());
        Assert.Equal("4bc149651c730bffa46308d38a3f2a2c5e9b6e08", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal(8, report.GetProperty("sampleCount").GetInt32());
        Assert.Equal(384, report.GetProperty("dimensions").GetInt32());

        var thresholds = report.GetProperty("thresholds");
        var results = report.GetProperty("results");
        Assert.True(
            results.GetProperty("maximumAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("minimumCosineSimilarity").GetDouble() >=
            thresholds.GetProperty("minimumCosineSimilarity").GetDouble());
    }

    [Fact]
    public async Task BgeSmallEnV15_ReportMeetsPublishedThresholds()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "bge-small-en-v1.5",
            "certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("BAAI/bge-small-en-v1.5", report.GetProperty("modelId").GetString());
        Assert.Equal("160f4d645d32abe3cabc5af6b6b39823eadf3c0e", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal(8, report.GetProperty("sampleCount").GetInt32());
        Assert.Equal(384, report.GetProperty("dimensions").GetInt32());

        var thresholds = report.GetProperty("thresholds");
        var results = report.GetProperty("results");
        Assert.True(
            results.GetProperty("maximumAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("minimumCosineSimilarity").GetDouble() >=
            thresholds.GetProperty("minimumCosineSimilarity").GetDouble());
    }

    [Fact]
    public async Task DistilBertSst2_ReportMeetsPublishedThresholds()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "distilbert-sst2",
            "certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal(
            "distilbert/distilbert-base-uncased-finetuned-sst-2-english",
            report.GetProperty("modelId").GetString());
        Assert.Equal("ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal(8, report.GetProperty("sampleCount").GetInt32());
        Assert.Equal(2, report.GetProperty("labelCount").GetInt32());

        var thresholds = report.GetProperty("thresholds");
        var results = report.GetProperty("results");
        Assert.True(
            results.GetProperty("maximumLogitAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumLogitAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("maximumProbabilityAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumProbabilityAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("labelAgreement").GetDouble() >=
            thresholds.GetProperty("minimumLabelAgreement").GetDouble());
    }

    [Fact]
    public async Task MobileNetV2ImageClassification_ReportMeetsPublishedThresholds()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "mobilenet-v2-image-classification",
            "certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("onnx-community/mobilenet_v2_1.0_224-ONNX", report.GetProperty("modelId").GetString());
        Assert.Equal("ba6621a361183d7ce00314802b63a46eee2aa48b", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal(3, report.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1001, report.GetProperty("labelCount").GetInt32());

        var thresholds = report.GetProperty("thresholds");
        var results = report.GetProperty("results");
        Assert.True(
            results.GetProperty("maximumLogitAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumLogitAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("maximumProbabilityAbsoluteError").GetDouble() <=
            thresholds.GetProperty("maximumProbabilityAbsoluteError").GetDouble());
        Assert.True(
            results.GetProperty("top1Agreement").GetDouble() >=
            thresholds.GetProperty("minimumTop1Agreement").GetDouble());
        Assert.True(
            results.GetProperty("minimumTop5Overlap").GetDouble() >=
            thresholds.GetProperty("minimumTop5Overlap").GetDouble());
    }

    [Fact]
    public async Task Qwen25Gguf_ReportMeetsPreviewHeaderPolicy()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "qwen2.5-0.5b-instruct-gguf",
            "certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("Qwen/Qwen2.5-0.5B-Instruct-GGUF", report.GetProperty("modelId").GetString());
        Assert.Equal("2e50b77b0eee3083842019e257b74854323d880a", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.True(report.GetProperty("artifact").GetProperty("verified").GetBoolean());

        var header = report.GetProperty("header");
        Assert.Equal(3u, header.GetProperty("version").GetUInt32());
        Assert.Equal("qwen2", header.GetProperty("architecture").GetString());
        Assert.Equal("Q2_K", header.GetProperty("fileTypeName").GetString());
        Assert.Equal("gpt2", header.GetProperty("tokenizerModel").GetString());
        Assert.True(header.GetProperty("hasChatTemplate").GetBoolean());

        var policy = report.GetProperty("policy");
        Assert.True(policy.GetProperty("allowed").GetBoolean());
        Assert.True(policy.GetProperty("loadVerificationRequired").GetBoolean());
    }

    [Fact]
    public async Task Qwen25GgufRuntime_ReportMeetsPublishedRequirements()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility",
            "qwen2.5-0.5b-instruct-gguf",
            "runtime-certification-report.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var report = document.RootElement;

        Assert.Equal("Qwen/Qwen2.5-0.5B-Instruct-GGUF", report.GetProperty("modelId").GetString());
        Assert.Equal("2e50b77b0eee3083842019e257b74854323d880a", report.GetProperty("revision").GetString());
        Assert.Equal("passed", report.GetProperty("status").GetString());

        var llamaCpp = report.GetProperty("llamaCpp");
        Assert.Equal("b10516", llamaCpp.GetProperty("release").GetString());
        Assert.Equal("b95502ba9", llamaCpp.GetProperty("commit").GetString());
        Assert.Equal("macos-arm64", llamaCpp.GetProperty("platform").GetString());
        Assert.True(llamaCpp.GetProperty("binaryVerified").GetBoolean());

        var artifact = report.GetProperty("artifact");
        Assert.True(artifact.GetProperty("verified").GetBoolean());
        Assert.Equal(415182688, artifact.GetProperty("size").GetInt64());

        var execution = report.GetProperty("execution");
        Assert.True(execution.GetProperty("loopbackOnly").GetBoolean());
        Assert.Equal(0, execution.GetProperty("gpuLayers").GetInt32());
        Assert.Equal(
            execution.GetProperty("nonStreaming").GetProperty("expected").GetString(),
            execution.GetProperty("nonStreaming").GetProperty("actual").GetString());
        Assert.Equal("stop", execution.GetProperty("nonStreaming").GetProperty("finishReason").GetString());
        Assert.Equal(
            execution.GetProperty("streaming").GetProperty("expected").GetString(),
            execution.GetProperty("streaming").GetProperty("actual").GetString());
        Assert.True(execution.GetProperty("streaming").GetProperty("terminalObserved").GetBoolean());
        Assert.Equal(
            execution.GetProperty("crashRecovery").GetProperty("expected").GetString(),
            execution.GetProperty("crashRecovery").GetProperty("actual").GetString());
        Assert.True(execution.GetProperty("crashRecovery").GetProperty("processRecovered").GetBoolean());
        Assert.True(execution.GetProperty("stoppedCleanly").GetBoolean());
    }
}
