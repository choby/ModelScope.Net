namespace ModelScope.Net.Runtime.Onnx;

public enum OnnxClassificationScoreMode
{
    Softmax,
    Sigmoid,
}

public sealed class OnnxTextClassificationOptions
{
    public string VocabFile { get; set; } = "vocab.txt";

    public string ConfigFile { get; set; } = "config.json";

    public string? OutputName { get; set; }

    public int MaxLength { get; set; } = 128;

    public int TopK { get; set; }

    public bool Lowercase { get; set; } = true;

    public OnnxClassificationScoreMode ScoreMode { get; set; } = OnnxClassificationScoreMode.Softmax;

    public string PadToken { get; set; } = "[PAD]";

    public string UnknownToken { get; set; } = "[UNK]";

    public string ClassToken { get; set; } = "[CLS]";

    public string SeparatorToken { get; set; } = "[SEP]";
}
