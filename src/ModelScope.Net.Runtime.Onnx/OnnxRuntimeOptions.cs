namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxRuntimeOptions
{
    public string? ModelFile { get; set; }

    public int IntraOpNumThreads { get; set; }

    public int InterOpNumThreads { get; set; }

    public int MaxConcurrentRuns { get; set; } = 1;

    public int MaxInputElements { get; set; } = 16 * 1024 * 1024;

    public ISet<string> CertifiedModels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
