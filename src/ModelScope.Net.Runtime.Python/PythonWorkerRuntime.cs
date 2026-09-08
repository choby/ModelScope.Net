using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Python;

/// <summary>
/// HTTP preview client for an isolated Python worker. The production gRPC
/// protocol and worker supervisor remain tracked by migration task P2-06.
/// </summary>
public sealed class PythonWorkerRuntime : IPythonWorkerRuntime, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly PythonWorkerOptions _options;
    private readonly IPythonWorkerSupervisor? _supervisor;
    private readonly string? _apiKey;

    public PythonWorkerRuntime(
        HttpClient httpClient,
        PythonWorkerOptions? options = null,
        IPythonWorkerSupervisor? supervisor = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options ?? new PythonWorkerOptions();
        _supervisor = supervisor;
        _apiKey = PythonWorkerSecurity.ResolveApiKey(_options, supervisor);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.RecoveryAttempts);
        if (_options.RecoveryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Recovery delay cannot be negative.");
        }

        if (_options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Request timeout must be positive.");
        }

        PythonWorkerSecurity.ValidateOptions(_options);
        if (_options.Tls.IsConfigured)
        {
            _httpClient = new HttpClient(PythonWorkerHttpHandlerFactory.Create(_options), disposeHandler: true)
            {
                Timeout = _options.RequestTimeout,
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public string Name => "python";

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        var detected = capabilities.RuntimeCandidates.Any(candidate =>
            string.Equals(candidate.RuntimeName, Name, StringComparison.OrdinalIgnoreCase));
        if (!detected)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, "No Python ecosystem artifacts were detected.", 50);
        }

        if (capabilities.ContainsRemoteCode && !_options.AllowRemoteCode)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, "Remote model code is disallowed by worker policy.", 50);
        }

        if (_options.Endpoint is null)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unavailable, "No isolated Python worker endpoint is configured.", 50);
        }

        var certified = capabilities.ModelId is not null && _options.CertifiedModels.Contains(capabilities.ModelId);
        return new RuntimeCandidate(
            Name,
            certified ? CapabilityStatus.Certified : CapabilityStatus.Compatible,
            certified ? "The model is certified for the Python worker." : "The Python worker must load-verify this model.",
            50);
    }

    public async Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var candidate = Evaluate(capabilities);
        if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
        {
            throw new ModelScopeException(
                candidate.Status == CapabilityStatus.Unavailable
                    ? ModelScopeErrorCode.RuntimeNotInstalled
                    : ModelScopeErrorCode.RemoteCodeNotAllowed,
                candidate.Reason);
        }

        if (_supervisor is not null)
        {
            await _supervisor.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        var endpoint = _options.Endpoint!;
        for (var attempt = 0; attempt <= _options.RecoveryAttempts; attempt++)
        {
            try
            {
                var health = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                if (!health.IsHealthy)
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.RuntimeNotInstalled,
                        health.Message ?? "Python worker is unhealthy.",
                        isRetryable: true);
                }

                using var loadRequest = CreateHttpRequest(
                    HttpMethod.Post,
                    new Uri(endpoint, "v1/models/load"),
                    JsonContent.Create(new
                    {
                        modelPath = capabilities.ModelPath,
                        modelId = capabilities.ModelId,
                        revision = capabilities.Revision,
                        allowRemoteCode = _options.AllowRemoteCode,
                    }));
                using var response = await _httpClient.SendAsync(
                    loadRequest,
                    cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, ModelScopeErrorCode.ModelLoadFailed, cancellationToken).ConfigureAwait(false);
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
                var sessionId = result.TryGetProperty("sessionId", out var value)
                    ? value.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "Python worker did not return a session ID.");
                }

                return new PythonWorkerSession(_httpClient, endpoint, sessionId, capabilities, _apiKey);
            }
            catch (Exception exception) when (
                attempt < _options.RecoveryAttempts &&
                IsRecoverable(exception) &&
                !cancellationToken.IsCancellationRequested)
            {
                if (_supervisor is not null)
                {
                    await _supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(_options.RecoveryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Python worker recovery loop ended unexpectedly.");
    }

    public async Task<PythonWorkerHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        if (_options.Endpoint is null)
        {
            return new PythonWorkerHealth(false, null, "No isolated Python worker endpoint is configured.");
        }

        using var healthRequest = CreateHttpRequest(HttpMethod.Get, new Uri(_options.Endpoint, "health"));
        using var response = await _httpClient.SendAsync(
            healthRequest,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new PythonWorkerHealth(false, null, $"Worker health returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var status = result.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : "healthy";
            var version = result.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
            var message = result.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            return new PythonWorkerHealth(
                status is "ok" or "healthy" or "ready",
                version,
                message);
        }
        catch (JsonException)
        {
            return new PythonWorkerHealth(false, null, "Worker health response is not valid JSON.");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException ||
        exception is ModelScopeException { IsRetryable: true };

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        ModelScopeErrorCode code,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        throw new ModelScopeException(
            code,
            $"Python worker returned HTTP {(int)response.StatusCode}.",
            response.StatusCode is System.Net.HttpStatusCode.BadGateway or
                System.Net.HttpStatusCode.ServiceUnavailable or
                System.Net.HttpStatusCode.GatewayTimeout);
    }


    private HttpRequestMessage CreateHttpRequest(HttpMethod method, Uri uri, HttpContent? content = null)
    {
        var message = new HttpRequestMessage(method, uri) { Content = content };
        PythonWorkerSecurity.ApplyHttpAuthorization(message, _apiKey);
        return message;
    }

    private sealed class PythonWorkerSession : IModelSession
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _endpoint;
        private readonly string _sessionId;
        private readonly string? _apiKey;

        public PythonWorkerSession(
            HttpClient httpClient,
            Uri endpoint,
            string sessionId,
            ModelCapabilities capabilities,
            string? apiKey)
        {
            _httpClient = httpClient;
            _endpoint = endpoint;
            _sessionId = sessionId;
            _apiKey = apiKey;
            Capabilities = capabilities;
        }

        public ModelCapabilities Capabilities { get; }

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            using var message = CreateRequest(
                HttpMethod.Post,
                new Uri(_endpoint, $"v1/sessions/{Uri.EscapeDataString(_sessionId)}/invoke"),
                JsonContent.Create(request));
            using var response = await _httpClient.SendAsync(
                message,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ModelScopeErrorCode.InferenceFailed, cancellationToken).ConfigureAwait(false);
            var output = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ModelResponse(
                output,
                Capabilities.ModelId ?? Capabilities.ModelPath,
                Capabilities.Revision ?? "local",
                "python",
                DateTimeOffset.UtcNow - started);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_endpoint, $"v1/sessions/{Uri.EscapeDataString(_sessionId)}/stream"))
            {
                Content = JsonContent.Create(request),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            PythonWorkerSecurity.ApplyHttpAuthorization(message, _apiKey);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ModelScopeErrorCode.InferenceFailed, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    yield return new ModelStreamEvent("done", EmptyJson(), true);
                    yield break;
                }

                using var document = JsonDocument.Parse(data);
                yield return new ModelStreamEvent("data", document.RootElement.Clone());
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var message = CreateRequest(
                    HttpMethod.Delete,
                    new Uri(_endpoint, $"v1/sessions/{Uri.EscapeDataString(_sessionId)}"));
                using var response = await _httpClient.SendAsync(message).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Worker cleanup is best effort; the supervisor owns final reclamation.
            }
        }

        private static JsonElement EmptyJson()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, HttpContent? content = null)
        {
            var message = new HttpRequestMessage(method, uri) { Content = content };
            PythonWorkerSecurity.ApplyHttpAuthorization(message, _apiKey);
            return message;
        }
    }
}
