using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxTextClassificationRuntimeTests
{
    private const string ClassificationModelBase64 =
        "CAo6vQIKJwoJaW5wdXRfaWRzEglmbG9hdF9pZHMiBENhc3QqCQoCdG8YAaABAgoyCglmbG9hdF9pZHMKBGF4ZXMSA3N1bSIJUmVkdWNlU3VtKg8KCGtlZXBkaW1zGAGgAQIKFAoDc3VtEghuZWdhdGl2ZSIDTmVnCiwKCG5lZ2F0aXZlCgNzdW0SBmxvZ2l0cyIGQ29uY2F0KgsKBGF4aXMYAaABAhITY2xhc3NpZmljYXRpb24tZ29sZCoNCAEQBzoBAUIEYXhlc1ooCglpbnB1dF9pZHMSGwoZCAcSFQoHEgViYXRjaAoKEghzZXF1ZW5jZVotCg5hdHRlbnRpb25fbWFzaxIbChkIBxIVCgcSBWJhdGNoCgoSCHNlcXVlbmNlYh0KBmxvZ2l0cxITChEIARINCgcSBWJhdGNoCgIIAkIECgAQDQ==";

    [Fact]
    public async Task WordPieceSoftmaxAndLabels_MatchNumericalGold()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var runtime = new OnnxTextClassificationRuntime(new OnnxRuntimeAdapter());
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(CreateCapabilities(directory)).Status);

            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            using var payload = JsonDocument.Parse("""{ "texts": ["hello", "bad"] }""");
            var response = await session.InvokeAsync(
                new ModelRequest("text-classification", payload.RootElement.Clone()));

            Assert.Equal("onnx-text-classification", response.Runtime);
            Assert.Equal(2, response.Output.GetProperty("count").GetInt32());
            Assert.Equal(2, response.Output.GetProperty("labelCount").GetInt32());
            Assert.Equal(-9f, response.Output.GetProperty("logits")[0][0].GetSingle());
            Assert.Equal(9f, response.Output.GetProperty("logits")[0][1].GetSingle());
            Assert.Equal("POSITIVE", response.Output.GetProperty("predictions")[0].GetProperty("label").GetString());
            Assert.True(response.Output.GetProperty("predictions")[0].GetProperty("score").GetDouble() > 0.999999);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Streaming_ReturnsOneTerminalClassificationEvent()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var runtime = new OnnxTextClassificationRuntime(new OnnxRuntimeAdapter());
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            var request = new ModelRequest(
                "text-classification",
                JsonSerializer.SerializeToElement(new { text = "hello" }),
                Stream: true);
            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(request)) events.Add(item);

            var terminal = Assert.Single(events);
            Assert.True(terminal.IsTerminal);
            Assert.Equal("classifications", terminal.Event);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RequiresVocabularyAndRejectsEscapingPath()
    {
        var directory = CreateModelDirectory(includeVocabulary: false);
        try
        {
            var missing = new OnnxTextClassificationRuntime(new OnnxRuntimeAdapter())
                .Evaluate(CreateCapabilities(directory));
            var escaping = new OnnxTextClassificationRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxTextClassificationOptions { VocabFile = "../vocab.txt" })
                .Evaluate(CreateCapabilities(directory));

            Assert.Equal(CapabilityStatus.Unavailable, missing.Status);
            Assert.Equal(CapabilityStatus.Unsupported, escaping.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RejectsEmbeddingModelCapabilities()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var capabilities = CreateCapabilities(directory) with
            {
                Task = "sentence-embedding",
                Architectures = ["BertModel"],
            };

            var candidate = new OnnxTextClassificationRuntime(new OnnxRuntimeAdapter())
                .Evaluate(capabilities);

            Assert.Equal(CapabilityStatus.Unsupported, candidate.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/wordpiece-classification",
        "fixture-v1",
        "text-classification",
        ["BertForSequenceClassification"],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(bool includeVocabulary)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-classification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Convert.FromBase64String(ClassificationModelBase64));
        File.WriteAllText(Path.Combine(directory, "config.json"), """
            { "id2label": { "0": "NEGATIVE", "1": "POSITIVE" } }
            """);
        if (includeVocabulary)
            File.WriteAllText(Path.Combine(directory, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\nbad\n");
        return directory;
    }
}
