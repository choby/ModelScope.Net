namespace ModelScope.Net.Runtime.Gguf;

public sealed record GgufCompatibilityResult(bool IsAllowed, string Reason);

public sealed class GgufCompatibilityPolicy
{
    public ISet<uint> Versions { get; } = new HashSet<uint> { 3 };

    public ISet<string> Architectures { get; } = new HashSet<string>(StringComparer.Ordinal) { "qwen2" };

    public ISet<uint> FileTypes { get; } = new HashSet<uint> { 10 };

    public ISet<string> TokenizerModels { get; } = new HashSet<string>(StringComparer.Ordinal) { "gpt2" };

    public bool RequireChatTemplate { get; set; } = true;

    public GgufCompatibilityResult Evaluate(GgufHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!Versions.Contains(header.Version))
            return Reject($"GGUF version {header.Version} is not in the preview whitelist.");
        if (string.IsNullOrWhiteSpace(header.Architecture))
            return Reject("The required general.architecture metadata is missing.");
        if (!Architectures.Contains(header.Architecture))
            return Reject($"GGUF architecture '{header.Architecture}' is not in the preview whitelist.");
        if (header.FileType is null)
            return Reject("The required general.file_type metadata is missing.");
        if (!FileTypes.Contains(header.FileType.Value))
            return Reject($"GGUF file type '{header.FileTypeName}' is not in the preview whitelist.");
        if (string.IsNullOrWhiteSpace(header.TokenizerModel))
            return Reject("The required tokenizer.ggml.model metadata is missing.");
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
