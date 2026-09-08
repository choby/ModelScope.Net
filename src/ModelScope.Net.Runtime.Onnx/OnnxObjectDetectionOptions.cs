namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxObjectDetectionOptions
{
    public string ConfigFile { get; set; } = "config.json";

    public string? InputName { get; set; }

    public string? OutputName { get; set; }

    public int InputHeight { get; set; } = 640;

    public int InputWidth { get; set; } = 640;

    public float RescaleFactor { get; set; } = 1f / 255f;

    public byte LetterboxPadValue { get; set; } = 114;

    public float ConfidenceThreshold { get; set; } = 0.25f;

    public float IouThreshold { get; set; } = 0.45f;

    public int MaxDetections { get; set; } = 100;

    public int MaxBatchSize { get; set; } = 1;

    public int MaxEncodedImageBytes { get; set; } = 20 * 1024 * 1024;

    public long MaxSourcePixels { get; set; } = 40_000_000;
}
