namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxRuntimeOptions
{
    public string? ModelFile { get; set; }

    public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.Cpu;

    public int DeviceId { get; set; }

    public bool AllowCpuFallback { get; set; }

    public long? GpuMemoryLimitBytes { get; set; }

    public int IntraOpNumThreads { get; set; }

    public int InterOpNumThreads { get; set; }

    public int MaxConcurrentRuns { get; set; } = 1;

    public int MaxInputElements { get; set; } = 16 * 1024 * 1024;

    public ISet<string> CertifiedModels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
