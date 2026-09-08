namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxTextGenerationOptions
{
    public string? ModelFile { get; set; }
    public string VocabularyFile { get; set; } = "vocab.json";
    public string MergesFile { get; set; } = "merges.txt";
    public int MaxPromptTokens { get; set; } = 64;
    public int DefaultMaxNewTokens { get; set; } = 16;
    public int MaxNewTokens { get; set; } = 32;
    public int MaxContextTokens { get; set; } = 96;
    public int EndTokenId { get; set; }
    public int KeyValueHeads { get; set; } = 3;
    public int HeadDimension { get; set; } = 64;
    public int LayerCount { get; set; } = 30;
}
