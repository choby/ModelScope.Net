namespace ModelScope.Net.Runtime.Onnx;

public enum OnnxEmbeddingPooling
{
    Mean,
    Cls,
}

public sealed class OnnxEmbeddingOptions
{
    public string VocabFile { get; set; } = "vocab.txt";

    public string? OutputName { get; set; }

    public int MaxLength { get; set; } = 128;

    public bool Lowercase { get; set; } = true;

    public bool Normalize { get; set; } = true;

    public OnnxEmbeddingPooling Pooling { get; set; } = OnnxEmbeddingPooling.Mean;

    public string PadToken { get; set; } = "[PAD]";

    public string UnknownToken { get; set; } = "[UNK]";

    public string ClassToken { get; set; } = "[CLS]";

    public string SeparatorToken { get; set; } = "[SEP]";
}
