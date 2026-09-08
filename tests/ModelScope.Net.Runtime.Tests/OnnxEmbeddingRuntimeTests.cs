using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxEmbeddingRuntimeTests
{
    private const string EmbeddingModelBase64 =
        "CAoSFE1vZGVsU2NvcGUuTmV0LlRlc3RzOvYCCicKCWlucHV0X2lkcxIJaWRzX2Zsb2F0IgRDYXN0KgkKAnRvGAGgAQIKKAoJaWRzX2Zsb2F0CgRheGVzEgppZHNfaGlkZGVuIglVbnNxdWVlemUKIgoKaWRzX2hpZGRlbgoDdHdvEgppZHNfZG91YmxlIgNNdWwKQAoKaWRzX2hpZGRlbgoKaWRzX2RvdWJsZRIRbGFzdF9oaWRkZW5fc3RhdGUiBkNvbmNhdCoLCgRheGlzGAKgAQISDmVtYmVkZGluZy1nb2xkKg0IARAHOgECQgRheGVzKg0QASIEAAAAQEIDdHdvWigKCWlucHV0X2lkcxIbChkIBxIVCgcSBWJhdGNoCgoSCHNlcXVlbmNlWi0KDmF0dGVudGlvbl9tYXNrEhsKGQgHEhUKBxIFYmF0Y2gKChIIc2VxdWVuY2ViNAoRbGFzdF9oaWRkZW5fc3RhdGUSHwodCAESGQoHEgViYXRjaAoKEghzZXF1ZW5jZQoCCAJCBAoAEA0=";

    [Fact]
    public async Task WordPieceMeanPooling_MatchesNumericalGold()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var capabilities = CreateCapabilities(directory);
            var runtime = new OnnxEmbeddingRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxEmbeddingOptions { Normalize = false });
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(capabilities).Status);

            await using var session = await runtime.CreateSessionAsync(capabilities);
            using var payload = JsonDocument.Parse("""{ "texts": ["hello worlds", "hello"] }""");
            var response = await session.InvokeAsync(new ModelRequest("sentence-embedding", payload.RootElement.Clone()));

            var embeddings = response.Output.GetProperty("embeddings").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(item => item.GetSingle()).ToArray())
                .ToArray();
            Assert.Equal([4f, 8f], embeddings[0]);
            Assert.Equal([3f, 6f], embeddings[1]);
            Assert.Equal(2, response.Output.GetProperty("dimensions").GetInt32());
            Assert.Equal("onnx-embedding", response.Runtime);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Normalization_ProducesUnitLengthAndStreamingTerminalEvent()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var runtime = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter());
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            using var payload = JsonDocument.Parse("""{ "text": "hello" }""");
            var response = await session.InvokeAsync(new ModelRequest("sentence-embedding", payload.RootElement.Clone()));
            var embedding = response.Output.GetProperty("embeddings")[0].EnumerateArray().Select(item => item.GetDouble()).ToArray();
            Assert.Equal(1d, Math.Sqrt(embedding.Sum(value => value * value)), precision: 6);

            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(new ModelRequest("sentence-embedding", payload.RootElement.Clone(), Stream: true)))
                events.Add(item);
            Assert.Single(events);
            Assert.True(events[0].IsTerminal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RequiresVocabularyInsideModelDirectory()
    {
        var directory = CreateModelDirectory(includeVocabulary: false);
        try
        {
            var candidate = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter()).Evaluate(CreateCapabilities(directory));
            Assert.Equal(CapabilityStatus.Unavailable, candidate.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RejectsTextClassificationModelCapabilities()
    {
        var directory = CreateModelDirectory(includeVocabulary: true);
        try
        {
            var capabilities = CreateCapabilities(directory) with
            {
                Task = "text-classification",
                Architectures = ["BertForSequenceClassification"],
            };

            var candidate = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter()).Evaluate(capabilities);

            Assert.Equal(CapabilityStatus.Unsupported, candidate.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/wordpiece-embedding",
        "fixture-v1",
        "sentence-embedding",
        ["BertModel"],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(bool includeVocabulary)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-embedding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Convert.FromBase64String(EmbeddingModelBase64));
        if (includeVocabulary)
        {
            File.WriteAllText(
                Path.Combine(directory, "vocab.txt"),
                "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\nworld\n##s\n你\n好\n");
        }
        return directory;
    }
}
