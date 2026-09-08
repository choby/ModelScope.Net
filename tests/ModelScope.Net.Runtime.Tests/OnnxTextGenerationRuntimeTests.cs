using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxTextGenerationRuntimeTests
{
    [Fact]
    public void ByteLevelBpeRoundTripsUtf8AndAppliesRankedMerges()
    {
        var directory = CreateTokenizerDirectory();
        try
        {
            var tokenizer = ByteLevelBpeTokenizer.Load(Path.Combine(directory, "vocab.json"), Path.Combine(directory, "merges.txt"));
            var text = "héllo 世界";
            Assert.Equal(text, tokenizer.Decode(tokenizer.Encode(text)));
            Assert.Single(tokenizer.Encode("hello"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EvaluateRequiresCausalTaskOnnxAndTokenizerAssets()
    {
        var directory = CreateTokenizerDirectory(); File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [0]);
        try
        {
            var runtime = new OnnxTextGenerationRuntime();
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(Capabilities(directory)).Status);
            Assert.Equal(CapabilityStatus.Unsupported, runtime.Evaluate(Capabilities(directory) with
            { Task = "sentence-embedding", Architectures = ["BertModel"] }).Status);
            File.Delete(Path.Combine(directory, "merges.txt"));
            Assert.Equal(CapabilityStatus.Unavailable, runtime.Evaluate(Capabilities(directory)).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RejectsEscapingAssetsAndInvalidResourceLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxTextGenerationRuntime(new() { MaxPromptTokens = 0 }));
        var directory = CreateTokenizerDirectory(); File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [0]);
        try
        {
            var candidate = new OnnxTextGenerationRuntime(new() { ModelFile = "model.onnx", VocabularyFile = "../vocab.json" })
                .Evaluate(Capabilities(directory));
            Assert.Equal(CapabilityStatus.Unsupported, candidate.Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ModelCapabilities Capabilities(string directory) => new(directory, "tests/causal", "fixture", "text-generation",
        ["LlamaForCausalLM"], [new("model.onnx", ModelArtifactFormat.Onnx, 1)], [], false, []);

    private static string CreateTokenizerDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-bpe-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        var visible = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToList();
        var values = visible.ToList(); var symbols = visible.ToList(); var extra = 0;
        for (var value = 0; value < 256; value++) if (!visible.Contains(value)) { values.Add(value); symbols.Add(256 + extra++); }
        var vocabulary = new Dictionary<string, int>();
        for (var index = 0; index < values.Count; index++) vocabulary[((char)symbols[index]).ToString()] = index;
        foreach (var token in new[] { "he", "hel", "hell", "hello" }) vocabulary[token] = vocabulary.Count;
        File.WriteAllText(Path.Combine(directory, "vocab.json"), JsonSerializer.Serialize(vocabulary));
        File.WriteAllText(Path.Combine(directory, "merges.txt"), "#version: 0.2\nh e\nhe l\nhel l\nhell o\n");
        return directory;
    }
}
