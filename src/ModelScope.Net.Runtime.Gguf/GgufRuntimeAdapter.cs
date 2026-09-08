using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Gguf;

public sealed class GgufRuntimeOptions
{
    public Uri? LlamaServerEndpoint { get; set; }

    public string ModelAlias { get; set; } = "modelscope-net";

    public string? ApiKey { get; set; }

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public int DefaultMaxTokens { get; set; } = 128;

    public int MaxTokens { get; set; } = 1024;

    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    public bool AllowNonLoopbackEndpoint { get; set; }

    public bool RequireHeaderPolicy { get; set; } = true;

    public GgufCompatibilityPolicy HeaderPolicy { get; } = new();

    public ISet<string> CertifiedModels { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class GgufRuntimeAdapter : IModelRuntime
{
    private readonly HttpClient? _httpClient;
    private readonly GgufRuntimeOptions _options;
    private readonly ILlamaServerSupervisor? _supervisor;

    public GgufRuntimeAdapter(GgufRuntimeOptions? options = null)
        : this(null, options, null)
    {
    }

    public GgufRuntimeAdapter(
        HttpClient? httpClient,
        GgufRuntimeOptions? options = null,
        ILlamaServerSupervisor? supervisor = null)
    {
        _httpClient = httpClient;
        _options = options ?? new GgufRuntimeOptions();
        _supervisor = supervisor;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ModelAlias);
        if (_options.StartupTimeout <= TimeSpan.Zero ||
            _options.RequestTimeout <= TimeSpan.Zero ||
            _options.HealthPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "GGUF runtime timeouts must be positive.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DefaultMaxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTokens, _options.DefaultMaxTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxResponseBytes, 1024);
    }

    public string Name => "gguf";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (!capabilities.Artifacts.Any(item => item.Format == ModelArtifactFormat.Gguf))
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, "No GGUF artifact was found.", 90);
        if (_supervisor is null && _options.LlamaServerEndpoint is null)
        {
            return new RuntimeCandidate(
                Name,
                CapabilityStatus.Unavailable,
                "A GGUF artifact was found, but a managed or external llama-server is not configured.",
                90);
        }

        var certified = capabilities.ModelId is not null &&
            _options.CertifiedModels.Contains(capabilities.ModelId);
        return new RuntimeCandidate(
            Name,
            certified ? CapabilityStatus.Certified : CapabilityStatus.Compatible,
            certified
                ? "The fixed model Revision is certified for the configured llama-server path."
                : "GGUF header and llama-server load verification are required.",
            90);
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
                    : ModelScopeErrorCode.ArchitectureUnsupported,
                candidate.Reason);
        }

        var modelFile = ResolveModelFile(capabilities);
        if (_options.RequireHeaderPolicy)
        {
            GgufHeader header;
            try
            {
                header = await GgufHeader.ReadAsync(modelFile, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"The GGUF header could not be verified: {exception.Message}",
                    innerException: exception);
            }
            var result = _options.HeaderPolicy.Evaluate(header, ResolveWorkload(capabilities.Task));
            if (!result.IsAllowed)
                throw new ModelScopeException(ModelScopeErrorCode.ArchitectureUnsupported, result.Reason);
        }

        var endpoint = _supervisor?.Endpoint ?? _options.LlamaServerEndpoint!;
        ValidateEndpoint(endpoint);
        if (_supervisor is not null)
            await _supervisor.EnsureRunningAsync(modelFile, cancellationToken).ConfigureAwait(false);

        var client = _httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var ownsClient = _httpClient is null;
        try
        {
            await WaitForHealthAsync(client, endpoint, cancellationToken).ConfigureAwait(false);
            return new GgufModelSession(
                client,
                ownsClient,
                endpoint,
                _supervisor?.ModelAlias ?? _options.ModelAlias,
                _supervisor?.ApiKey ?? _options.ApiKey,
                _options,
                _supervisor,
                modelFile,
                capabilities,
                ResolveWorkload(capabilities.Task));
        }
        catch
        {
            if (ownsClient) client.Dispose();
            throw;
        }
    }

    private async Task WaitForHealthAsync(
        HttpClient client,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        while (true)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "health"));
                AddAuthorization(message);
                using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    await ThrowForResponseAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (_supervisor is not null)
            {
                // A managed server may still be opening its listener.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"llama-server health check did not succeed within {_options.StartupTimeout}.",
                    isRetryable: true);
            }

            try
            {
                await Task.Delay(_options.HealthPollInterval, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"llama-server health check did not succeed within {_options.StartupTimeout}.",
                    isRetryable: true);
            }
        }
    }

    private string ResolveModelFile(ModelCapabilities capabilities)
    {
        var artifact = capabilities.Artifacts.First(item => item.Format == ModelArtifactFormat.Gguf);
        var root = Path.GetFullPath(capabilities.ModelPath);
        var path = Path.GetFullPath(Path.Combine(root, artifact.Path));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "The GGUF artifact path escapes the model directory.");
        }
        if (!File.Exists(path))
            throw new ModelScopeException(ModelScopeErrorCode.ModelNotFound, "The GGUF artifact does not exist.");
        return path;
    }

    private void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "llama-server must use HTTP or HTTPS.");
        if (!_options.AllowNonLoopbackEndpoint && !endpoint.IsLoopback)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "Non-loopback llama-server endpoints require explicit opt-in.");
        }
    }

    private void AddAuthorization(HttpRequestMessage message)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    internal static GgufWorkload ResolveWorkload(string? task) =>
        IsEmbeddingTask(task) ? GgufWorkload.Embedding : GgufWorkload.TextGeneration;

    public static bool IsEmbeddingTask(string? task) =>
        task is not null && (
            task.Equals("feature-extraction", StringComparison.OrdinalIgnoreCase) ||
            task.Equals("sentence-embedding", StringComparison.OrdinalIgnoreCase) ||
            task.Equals("embedding", StringComparison.OrdinalIgnoreCase) ||
            task.Equals("text-embedding", StringComparison.OrdinalIgnoreCase));

    private static Task ThrowForResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var code = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => ModelScopeErrorCode.InvalidRequest,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelScopeErrorCode.AuthenticationRequired,
            HttpStatusCode.ServiceUnavailable => ModelScopeErrorCode.ModelLoadFailed,
            _ => ModelScopeErrorCode.InferenceFailed,
        };
        throw new ModelScopeException(
            code,
            $"llama-server returned HTTP {(int)response.StatusCode}.",
            response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);
    }


    private sealed class GgufModelSession(
        HttpClient client,
        bool ownsClient,
        Uri endpoint,
        string modelAlias,
        string? apiKey,
        GgufRuntimeOptions options,
        ILlamaServerSupervisor? supervisor,
        string modelFile,
        ModelCapabilities capabilities,
        GgufWorkload workload) : IModelSession
    {
        public ModelCapabilities Capabilities { get; } = capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateTask(request.Task);
            await EnsureManagedServerAsync(cancellationToken).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                using var message = CreateRequest(request, stream: false);
                using var response = await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    await ThrowForResponseAsync(response, timeout.Token).ConfigureAwait(false);
                var bytes = await ReadLimitedAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    options.MaxResponseBytes,
                    timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                return new ModelResponse(
                    document.RootElement.Clone(),
                    Capabilities.ModelId ?? modelAlias,
                    Capabilities.Revision ?? "local",
                    "gguf",
                    Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InferenceFailed,
                    $"llama-server request exceeded {options.RequestTimeout}.",
                    isRetryable: true);
            }
            catch (HttpRequestException exception)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InferenceFailed,
                    "The llama-server connection failed. A managed server will be restarted on the next call.",
                    isRetryable: true,
                    innerException: exception);
            }
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (workload == GgufWorkload.Embedding)
            {
                var embedding = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                yield return new ModelStreamEvent("embedding", embedding.Output, IsTerminal: true);
                yield break;
            }

            ValidateTask(request.Task);
            await EnsureManagedServerAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var message = CreateRequest(request, stream: true);
            using var response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                await ThrowForResponseAsync(response, timeout.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var observedCharacters = 0L;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) yield break;
                observedCharacters += line.Length;
                if (observedCharacters > options.MaxResponseBytes)
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.InferenceFailed,
                        $"llama-server stream exceeded the {options.MaxResponseBytes} character limit.");
                }
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    yield return new ModelStreamEvent("done", EmptyJson(), IsTerminal: true);
                    yield break;
                }
                using var document = JsonDocument.Parse(data);
                yield return new ModelStreamEvent("data", document.RootElement.Clone());
            }
        }

        public ValueTask DisposeAsync()
        {
            if (ownsClient) client.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task EnsureManagedServerAsync(CancellationToken cancellationToken)
        {
            if (supervisor is not null)
                await supervisor.EnsureRunningAsync(modelFile, cancellationToken).ConfigureAwait(false);
        }

        private HttpRequestMessage CreateRequest(ModelRequest request, bool stream)
        {
            if (request.Payload.ValueKind != JsonValueKind.Object)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "GGUF generation input must be a JSON object.");
            }
            var usesChat = request.Task.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
                request.Task.Equals("chat-completion", StringComparison.OrdinalIgnoreCase) ||
                request.Payload.TryGetProperty("messages", out _);
            var route = workload == GgufWorkload.Embedding
                ? "v1/embeddings"
                : usesChat ? "v1/chat/completions" : "v1/completions";
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, route));
            if (!string.IsNullOrWhiteSpace(apiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            message.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            message.Content = JsonContent.Create(CreatePayload(request, stream));
            return message;
        }

        private JsonElement CreatePayload(ModelRequest request, bool stream)
        {
            var hasMaxTokens = false;
            var hasInput = false;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", modelAlias);
                foreach (var property in request.Payload.EnumerateObject())
                {
                    if (property.NameEquals("model") || property.NameEquals("stream")) continue;
                    if (workload == GgufWorkload.Embedding &&
                        (property.NameEquals("text") || property.NameEquals("prompt")) &&
                        !request.Payload.TryGetProperty("input", out _))
                    {
                        writer.WritePropertyName("input");
                        property.Value.WriteTo(writer);
                        hasInput = true;
                        continue;
                    }
                    if (property.NameEquals("input")) hasInput = true;
                    if (property.NameEquals("max_tokens"))
                    {
                        if (workload == GgufWorkload.Embedding) continue;
                        if (!property.Value.TryGetInt32(out var maxTokens) ||
                            maxTokens < 1 || maxTokens > options.MaxTokens)
                        {
                            throw new ModelScopeException(
                                ModelScopeErrorCode.InvalidRequest,
                                $"max_tokens must be between 1 and {options.MaxTokens}.");
                        }
                        hasMaxTokens = true;
                    }
                    property.WriteTo(writer);
                }
                if (workload == GgufWorkload.Embedding)
                {
                    if (!hasInput)
                    {
                        throw new ModelScopeException(
                            ModelScopeErrorCode.InvalidRequest,
                            "GGUF embedding input must contain 'input', 'text', or 'prompt'.");
                    }
                }
                else
                {
                    if (!hasMaxTokens) writer.WriteNumber("max_tokens", options.DefaultMaxTokens);
                    writer.WriteBoolean("stream", stream);
                }
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        private void ValidateTask(string task)
        {
            if (workload == GgufWorkload.Embedding)
            {
                if (!IsEmbeddingTask(task))
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.OperatorUnsupported,
                        "The GGUF embedding runtime only supports feature-extraction and embedding tasks.");
                }
                return;
            }

            if (task is null || !(task.Equals("text-generation", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("completion", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("chat-completion", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.OperatorUnsupported,
                    "The GGUF preview runtime only supports text generation and chat completion.");
            }
        }

        private static async Task<byte[]> ReadLimitedAsync(
            Stream stream,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length + read > maximumBytes)
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.InferenceFailed,
                        $"llama-server response exceeded the {maximumBytes} byte limit.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        private static JsonElement EmptyJson()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }
}
