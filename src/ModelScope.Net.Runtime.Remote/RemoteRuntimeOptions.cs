namespace ModelScope.Net.Runtime.Remote;

public sealed class RemoteRuntimeOptions
{
    public Uri Endpoint { get; set; } = new("https://api-inference.modelscope.cn");

    public string? Token { get; set; }

    public ISet<string> CertifiedModels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
