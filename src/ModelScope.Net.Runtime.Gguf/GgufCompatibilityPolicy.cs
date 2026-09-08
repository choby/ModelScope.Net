namespace ModelScope.Net.Runtime.Gguf;

public enum GgufWorkload
{
    TextGeneration,
    Embedding,
}

public sealed record GgufCompatibilityResult(bool IsAllowed, string Reason);

public sealed class GgufCompatibilityPolicy
{
    public ISet<uint> Versions { get; } = new HashSet<uint> { 3 };

    public ISet<string> Architectures { get; } = new HashSet<string>(StringComparer.Ordinal) { "qwen2" };

    public ISet<uint> FileTypes { get; } = new HashSet<uint> { 10 };

    public ISet<string> TokenizerModels { get; } = new HashSet<string>(StringComparer.Ordinal) { "gpt2" };

    public bool RequireChatTemplate { get; set; } = true;

    public ISet<string> EmbeddingArchitectures { get; } = new HashSet<string>(StringComparer.Ordinal) { "bert" };

    public ISet<uint> EmbeddingFileTypes { get; } = new HashSet<uint> { 15 };

    public ISet<string> EmbeddingTokenizerModels { get; } = new HashSet<string>(StringComparer.Ordinal) { "bert" };

    public GgufCompatibilityResult Evaluate(GgufHeader header) => Evaluate(header, GgufWorkload.TextGeneration);

    public GgufCompatibilityResult Evaluate(GgufHeader header, GgufWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!Versions.Contains(header.Version))
            return Reject($"GGUF version {header.Version} is not in the preview whitelist.");
        if (string.IsNullOrWhiteSpace(header.Architecture))
            return Reject("The required general.architecture metadata is missing.");
        if (header.FileType is null)
            return Reject("The required general.file_type metadata is missing.");
        if (string.IsNullOrWhiteSpace(header.TokenizerModel))
            return Reject("The required tokenizer.ggml.model metadata is missing.");

        if (workload == GgufWorkload.Embedding)
        {
            if (!EmbeddingArchitectures.Contains(header.Architecture))
                return Reject($"GGUF embedding architecture '{header.Architecture}' is not in the preview whitelist.");
            if (!EmbeddingFileTypes.Contains(header.FileType.Value))
                return Reject($"GGUF embedding file type '{header.FileTypeName}' is not in the preview whitelist.");
            if (!EmbeddingTokenizerModels.Contains(header.TokenizerModel))
                return Reject($"GGUF embedding tokenizer '{header.TokenizerModel}' is not in the preview whitelist.");
            return new GgufCompatibilityResult(
                true,
                $"GGUF {header.Version} {header.Architecture}/{header.FileTypeName} embedding header is allowed by the preview policy; llama.cpp load verification is still required.");
        }

        if (!Architectures.Contains(header.Architecture))
            return Reject($"GGUF architecture '{header.Architecture}' is not in the preview whitelist.");
        if (!FileTypes.Contains(header.FileType.Value))
            return Reject($"GGUF file type '{header.FileTypeName}' is not in the preview whitelist.");
        if (!TokenizerModels.Contains(header.TokenizerModel))
            return Reject($"GGUF tokenizer '{header.TokenizerModel}' is not in the preview whitelist.");
        if (RequireChatTemplate && !header.HasChatTemplate)
            return Reject("The required tokenizer.chat_template metadata is missing.");
        return new GgufCompatibilityResult(
            true,
            $"GGUF {header.Version} {header.Architecture}/{header.FileTypeName} header is allowed by the preview policy; llama.cpp load verification is still required.");
    }

    private static GgufCompatibilityResult Reject(string reason) => new(false, reason);
}
