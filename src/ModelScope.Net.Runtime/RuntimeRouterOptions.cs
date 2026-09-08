namespace ModelScope.Net.Runtime;

public sealed class RuntimeRouterOptions
{
    public RuntimePolicyMode Mode { get; set; } = RuntimePolicyMode.Development;

    public bool AllowExperimentalInProduction { get; set; }

    public bool AllowRemoteFallback { get; set; }

    public bool AllowRemoteCode { get; set; }

    public ProductionRuntimePolicy? ProductionPolicy { get; set; }
}
