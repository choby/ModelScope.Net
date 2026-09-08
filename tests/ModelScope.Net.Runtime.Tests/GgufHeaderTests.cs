using System.Text;
using ModelScope.Net.Runtime.Gguf;

namespace ModelScope.Net.Runtime.Tests;

public sealed class GgufHeaderTests
{
    [Fact]
    public async Task ReadAsync_ParsesSelectedMetadataAndSkipsTokenizerArrays()
    {
        var path = CreateGguf("qwen2", fileType: 10, includeChatTemplate: true);
        try
        {
            var header = await GgufHeader.ReadAsync(path);

            Assert.Equal(3u, header.Version);
            Assert.Equal(42ul, header.TensorCount);
            Assert.Equal(6ul, header.MetadataCount);
            Assert.Equal("qwen2", header.Architecture);
            Assert.Equal(10u, header.FileType);
            Assert.Equal("Q2_K", header.FileTypeName);
            Assert.Equal(2u, header.QuantizationVersion);
            Assert.Equal("gpt2", header.TokenizerModel);
            Assert.True(header.HasChatTemplate);
            Assert.Contains("messages", header.ChatTemplate, StringComparison.Ordinal);
            Assert.True(header.MetadataEndOffset > 24);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CompatibilityPolicy_IsStrictAndExplicitlyExpandable()
    {
        var allowedPath = CreateGguf("qwen2", fileType: 10, includeChatTemplate: true);
        var architecturePath = CreateGguf("llama", fileType: 10, includeChatTemplate: true);
        var quantizationPath = CreateGguf("qwen2", fileType: 15, includeChatTemplate: true);
        try
        {
            var policy = new GgufCompatibilityPolicy();
            var allowed = policy.Evaluate(await GgufHeader.ReadAsync(allowedPath));
            var architectureRejected = policy.Evaluate(await GgufHeader.ReadAsync(architecturePath));
            var quantizationRejected = policy.Evaluate(await GgufHeader.ReadAsync(quantizationPath));

            Assert.True(allowed.IsAllowed);
            Assert.False(architectureRejected.IsAllowed);
            Assert.Contains("llama", architectureRejected.Reason, StringComparison.Ordinal);
            Assert.False(quantizationRejected.IsAllowed);
            Assert.Contains("Q4_K_M", quantizationRejected.Reason, StringComparison.Ordinal);

            policy.Architectures.Add("llama");
            policy.FileTypes.Add(15);
            Assert.True(policy.Evaluate(await GgufHeader.ReadAsync(architecturePath)).IsAllowed);
            Assert.True(policy.Evaluate(await GgufHeader.ReadAsync(quantizationPath)).IsAllowed);
        }
        finally
        {
            File.Delete(allowedPath);
            File.Delete(architecturePath);
            File.Delete(quantizationPath);
        }
    }

    [Fact]
    public async Task CompatibilityPolicy_AllowsBertEmbeddingWithoutChatTemplate()
    {
        var embeddingPath = CreateGguf("bert", fileType: 15, includeChatTemplate: false, tokenizer: "bert");
        var generationPath = CreateGguf("qwen2", fileType: 10, includeChatTemplate: true);
        try
        {
            var policy = new GgufCompatibilityPolicy();
            var embedding = policy.Evaluate(await GgufHeader.ReadAsync(embeddingPath), GgufWorkload.Embedding);
            var generationAsEmbedding = policy.Evaluate(await GgufHeader.ReadAsync(generationPath), GgufWorkload.Embedding);
            var embeddingAsGeneration = policy.Evaluate(await GgufHeader.ReadAsync(embeddingPath));

            Assert.True(embedding.IsAllowed);
            Assert.False(generationAsEmbedding.IsAllowed);
            Assert.Contains("qwen2", generationAsEmbedding.Reason, StringComparison.Ordinal);
            Assert.False(embeddingAsGeneration.IsAllowed);
            Assert.Contains("bert", embeddingAsGeneration.Reason, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(embeddingPath);
            File.Delete(generationPath);
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidMagicUnsupportedVersionAndExcessiveMetadata()
    {
        var invalidMagic = WriteHeader("NOPE", version: 3, metadataCount: 0);
        var invalidVersion = WriteHeader("GGUF", version: 99, metadataCount: 0);
        var excessiveMetadata = WriteHeader("GGUF", version: 3, metadataCount: 2);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => GgufHeader.ReadAsync(invalidMagic));
            await Assert.ThrowsAsync<InvalidDataException>(() => GgufHeader.ReadAsync(invalidVersion));
            await Assert.ThrowsAsync<InvalidDataException>(() => GgufHeader.ReadAsync(
                excessiveMetadata,
                new GgufReaderLimits { MaxMetadataEntries = 1 }));
        }
        finally
        {
            File.Delete(invalidMagic);
            File.Delete(invalidVersion);
            File.Delete(excessiveMetadata);
        }
    }

    private static string CreateGguf(
        string architecture,
        uint fileType,
        bool includeChatTemplate,
        string tokenizer = "gpt2")
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-{Guid.NewGuid():N}.gguf");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write("GGUF"u8);
        writer.Write(3u);
        writer.Write(42ul);
        writer.Write(includeChatTemplate ? 6ul : 5ul);
        WriteStringMetadata(writer, "general.architecture", architecture);
        WriteUInt32Metadata(writer, "general.file_type", fileType);
        WriteUInt32Metadata(writer, "general.quantization_version", 2);
        WriteStringMetadata(writer, "tokenizer.ggml.model", tokenizer);
        WriteStringArrayMetadata(writer, "tokenizer.ggml.tokens", ["hello", "world"]);
        if (includeChatTemplate)
            WriteStringMetadata(writer, "tokenizer.chat_template", "{% for message in messages %}{{ message.content }}{% endfor %}");
        return path;
    }

    private static string WriteHeader(string magic, uint version, ulong metadataCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-{Guid.NewGuid():N}.gguf");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes(magic));
        writer.Write(version);
        writer.Write(0ul);
        writer.Write(metadataCount);
        return path;
    }

    private static void WriteUInt32Metadata(BinaryWriter writer, string key, uint value)
    {
        WriteString(writer, key);
        writer.Write(4u);
        writer.Write(value);
    }

    private static void WriteStringMetadata(BinaryWriter writer, string key, string value)
    {
        WriteString(writer, key);
        writer.Write(8u);
        WriteString(writer, value);
    }

    private static void WriteStringArrayMetadata(BinaryWriter writer, string key, IReadOnlyList<string> values)
    {
        WriteString(writer, key);
        writer.Write(9u);
        writer.Write(8u);
        writer.Write((ulong)values.Count);
        foreach (var value in values) WriteString(writer, value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
