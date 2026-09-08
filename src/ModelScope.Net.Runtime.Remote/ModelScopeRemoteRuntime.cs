using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Remote;

public sealed class ModelScopeRemoteRuntime : IRemoteModelRuntime
{
    private readonly HttpClient _httpClient;
    private readonly RemoteRuntimeOptions _options;

    public ModelScopeRemoteRuntime(HttpClient httpClient, RemoteRuntimeOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new RemoteRuntimeOptions();
    }

    public string Name => "remote";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        var modelId = capabilities.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, "A Model ID is required for API Inference.", 70);
        }

        var status = _options.CertifiedModels.Contains(modelId)
            ? CapabilityStatus.Certified
            : CapabilityStatus.Detected;
        return new RuntimeCandidate(
            Name,
            status,
            status == CapabilityStatus.Certified
                ? "The model is certified for ModelScope API Inference."
                : "Remote capability must be confirmed with ModelScope API Inference.",
            70);
    }

    public Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var candidate = Evaluate(capabilities);
        if (candidate.Status == CapabilityStatus.Unsupported)
        {
            throw new ModelScopeException(ModelScopeErrorCode.ArchitectureUnsupported, candidate.Reason);
        }

        return Task.FromResult<IModelSession>(new RemoteModelSession(_httpClient, _options, capabilities));
    }

    private sealed class RemoteModelSession : IModelSession
    {
        private readonly HttpClient _httpClient;
        private readonly RemoteRuntimeOptions _options;

        public RemoteModelSession(
            HttpClient httpClient,
            RemoteRuntimeOptions options,
            ModelCapabilities capabilities)
        {
            _httpClient = httpClient;
            _options = options;
            Capabilities = capabilities;
        }

        public ModelCapabilities Capabilities { get; }

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            using var message = CreateRequest(request, stream: false);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ModelResponse(
                document.RootElement.Clone(),
                Capabilities.ModelId!,
                Capabilities.Revision ?? "remote",
                "remote",
                DateTimeOffset.UtcNow - started);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var message = CreateRequest(request, stream: true);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private HttpRequestMessage CreateRequest(ModelRequest request, bool stream)
        {
            var route = request.Task.ToLowerInvariant() switch
            {
                "text-generation" or "chat" or "chat-completion" => "v1/chat/completions",
                "embedding" or "text-embedding" => "v1/embeddings",
                "text-to-image" or "image-generation" => "v1/images/generations",
                _ => "v1/models/invoke",
            };
            var uri = new Uri(_options.Endpoint, route);
            var message = new HttpRequestMessage(HttpMethod.Post, uri);
            if (!string.IsNullOrWhiteSpace(_options.Token))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            }

            message.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            message.Content = new ByteArrayContent(CreatePayload(request, stream));
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return message;
        }

        private byte[] CreatePayload(ModelRequest request, bool stream)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", Capabilities.ModelId);
                if (request.Payload.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in request.Payload.EnumerateObject())
                    {
                        if (property.NameEquals("model") || property.NameEquals("stream"))
                        {
                            continue;
                        }

                        property.WriteTo(writer);
                    }
                }
                else
                {
                    writer.WritePropertyName("input");
                    request.Payload.WriteTo(writer);
                }

                writer.WriteBoolean("stream", stream);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        private static Task EnsureSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.IsSuccessStatusCode)
            {
                return Task.CompletedTask;
            }

            var code = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelScopeErrorCode.AuthenticationRequired,
                HttpStatusCode.NotFound => ModelScopeErrorCode.ModelNotFound,
                HttpStatusCode.TooManyRequests => ModelScopeErrorCode.RemoteApiRateLimited,
                _ => ModelScopeErrorCode.RemoteApiUnavailable,
            };
            throw new ModelScopeException(code, $"ModelScope API Inference returned HTTP {(int)response.StatusCode}.", code is ModelScopeErrorCode.RemoteApiRateLimited or ModelScopeErrorCode.RemoteApiUnavailable);
        }

        private static JsonElement EmptyJson()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

    }
}
