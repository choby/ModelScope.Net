namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxImageClassificationOptions
{
    public string ConfigFile { get; set; } = "config.json";

    public string PreprocessorConfigFile { get; set; } = "preprocessor_config.json";

    public string? OutputName { get; set; }

    public int TopK { get; set; } = 5;

    public int MaxBatchSize { get; set; } = 8;

    public int MaxEncodedImageBytes { get; set; } = 20 * 1024 * 1024;

    public long MaxSourcePixels { get; set; } = 40_000_000;
}
