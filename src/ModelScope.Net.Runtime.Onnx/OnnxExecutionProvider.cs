using Microsoft.ML.OnnxRuntime;

namespace ModelScope.Net.Runtime.Onnx;

public enum OnnxExecutionProvider
{
    Cpu,
    Cuda,
    TensorRt,
    DirectML,
    CoreML,
}

public sealed record OnnxExecutionProviderSelection(
    OnnxExecutionProvider Requested,
    OnnxExecutionProvider Effective,
    int DeviceId,
    bool UsedCpuFallback,
    IReadOnlyList<string> AvailableProviders)
{
    public string EffectiveName => OnnxExecutionProviderEnvironment.GetNativeName(Effective);

    public int RequestedDeviceId { get; init; }

    public string RuntimeVersion { get; init; } = string.Empty;
}

internal interface IOnnxExecutionProviderEnvironment
{
    IReadOnlyList<string> GetAvailableProviders();

    string GetRuntimeVersion();

    void Configure(
        SessionOptions sessionOptions,
        OnnxExecutionProvider provider,
        int deviceId,
        long? gpuMemoryLimitBytes,
        bool allowCpuFallback);
}

internal sealed class OnnxExecutionProviderEnvironment : IOnnxExecutionProviderEnvironment
{
    public IReadOnlyList<string> GetAvailableProviders() =>
        Array.AsReadOnly(OrtEnv.Instance().GetAvailableProviders());

    public string GetRuntimeVersion() => OrtEnv.Instance().GetVersionString();

    public void Configure(
        SessionOptions sessionOptions,
        OnnxExecutionProvider provider,
        int deviceId,
        long? gpuMemoryLimitBytes,
        bool allowCpuFallback)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);

        if (provider != OnnxExecutionProvider.Cpu && !allowCpuFallback)
        {
            sessionOptions.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        }

        switch (provider)
        {
            case OnnxExecutionProvider.Cpu:
                return;
            case OnnxExecutionProvider.Cuda:
                if (gpuMemoryLimitBytes is null)
                {
                    sessionOptions.AppendExecutionProvider_CUDA(deviceId);
                    return;
                }

                using (var options = new OrtCUDAProviderOptions())
                {
                    options.UpdateOptions(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["device_id"] = deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["gpu_mem_limit"] = gpuMemoryLimitBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                    sessionOptions.AppendExecutionProvider_CUDA(options);
                }
                return;
            case OnnxExecutionProvider.TensorRt:
                sessionOptions.AppendExecutionProvider_Tensorrt(deviceId);
                return;
            case OnnxExecutionProvider.DirectML:
                sessionOptions.EnableMemoryPattern = false;
                sessionOptions.AppendExecutionProvider_DML(deviceId);
                return;
            case OnnxExecutionProvider.CoreML:
                sessionOptions.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_NONE);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown ONNX execution provider.");
        }
    }

    internal static string GetNativeName(OnnxExecutionProvider provider) => provider switch
    {
        OnnxExecutionProvider.Cpu => "CPUExecutionProvider",
        OnnxExecutionProvider.Cuda => "CUDAExecutionProvider",
        OnnxExecutionProvider.TensorRt => "TensorrtExecutionProvider",
        OnnxExecutionProvider.DirectML => "DmlExecutionProvider",
        OnnxExecutionProvider.CoreML => "CoreMLExecutionProvider",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown ONNX execution provider."),
    };
}
