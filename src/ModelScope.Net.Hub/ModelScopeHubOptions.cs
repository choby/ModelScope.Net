namespace ModelScope.Net.Hub;

public sealed class ModelScopeHubOptions
{
    public Uri Endpoint { get; set; } = new("https://www.modelscope.cn");

    public string? Token { get; set; }

    public string DefaultRevision { get; set; } = "master";

    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "modelscope.net");

    public int MaxRetries { get; set; } = 5;

    public int MaxConcurrentDownloads { get; set; } = 4;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);

    internal void Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            (Endpoint.Scheme != Uri.UriSchemeHttps && Endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URI.", nameof(Endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(CacheDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentDownloads, 1);
        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }
    }
}
