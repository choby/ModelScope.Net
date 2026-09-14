using Microsoft.ML.OnnxRuntime;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

internal sealed class TestOnnxExecutionProviderEnvironment(params string[] providers)
    : IOnnxExecutionProviderEnvironment
{
    public OnnxExecutionProvider? ConfiguredProvider { get; private set; }

    public int? ConfiguredDeviceId { get; private set; }

    public long? ConfiguredGpuMemoryLimitBytes { get; private set; }

    public bool? ConfiguredAllowCpuFallback { get; private set; }

    public int ConfigureCalls { get; private set; }

    public Exception? ConfigureException { get; init; }

    public IReadOnlyList<string> GetAvailableProviders() => providers;

    public string GetRuntimeVersion() => "test-version";

    public void Configure(
        SessionOptions sessionOptions,
        OnnxExecutionProvider provider,
        int deviceId,
        long? gpuMemoryLimitBytes,
        bool allowCpuFallback)
    {
        ConfigureCalls++;
        if (ConfigureException is not null)
        {
            throw ConfigureException;
        }

        ConfiguredProvider = provider;
        ConfiguredDeviceId = deviceId;
        ConfiguredGpuMemoryLimitBytes = gpuMemoryLimitBytes;
        ConfiguredAllowCpuFallback = allowCpuFallback;
    }
}
