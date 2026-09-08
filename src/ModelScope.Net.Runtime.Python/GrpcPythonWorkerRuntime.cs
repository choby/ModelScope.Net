using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using ModelScope.Net.Runtime.Python.Grpc;

namespace ModelScope.Net.Runtime.Python;

public sealed class GrpcPythonWorkerRuntime : IPythonWorkerRuntime, IDisposable
{
    private readonly PythonWorkerOptions _options;
    private readonly IPythonWorkerSupervisor? _supervisor;
    private readonly HttpClient? _ownedHttpClient;
    private readonly GrpcChannel? _channel;
    private readonly Grpc.PythonWorker.PythonWorkerClient? _client;
    private readonly Metadata? _headers;

    public GrpcPythonWorkerRuntime(
        HttpClient httpClient,
        PythonWorkerOptions? options = null,
        IPythonWorkerSupervisor? supervisor = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options ?? new PythonWorkerOptions();
        _supervisor = supervisor;
        _headers = PythonWorkerSecurity.CreateGrpcHeaders(
            PythonWorkerSecurity.ResolveApiKey(_options, supervisor));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.RecoveryAttempts);
        if (_options.RecoveryDelay < TimeSpan.Zero || _options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Recovery delay and request timeout are invalid.");
        }

        if (_options.Endpoint is not null)
        {
            PythonWorkerSecurity.ValidateOptions(_options);
            var channelHttpClient = httpClient;
            if (_options.Tls.IsConfigured)
            {
                _ownedHttpClient = new HttpClient(
                    PythonWorkerHttpHandlerFactory.Create(_options),
                    disposeHandler: true);
                channelHttpClient = _ownedHttpClient;
            }

            _channel = GrpcChannel.ForAddress(_options.Endpoint, new GrpcChannelOptions
            {
                HttpClient = channelHttpClient,
                DisposeHttpClient = false,
            });
            _client = new Grpc.PythonWorker.PythonWorkerClient(_channel);
        }
    }

    public string Name => "python";

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

        if (_client is null)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unavailable, "No Python gRPC worker endpoint is configured.", 50);
        }

        var certified = capabilities.ModelId is not null && _options.CertifiedModels.Contains(capabilities.ModelId);
        return new RuntimeCandidate(
            Name,
            certified ? CapabilityStatus.Certified : CapabilityStatus.Compatible,
            certified ? "The model is certified for the Python gRPC worker." : "The Python gRPC worker must load-verify this model.",
            50);
    }

    public async Task<PythonWorkerHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return new PythonWorkerHealth(false, null, "No Python gRPC worker endpoint is configured.");
        }

        try
        {
            var response = await _client.HealthAsync(
                new HealthRequest(),
                headers: _headers,
                deadline: Deadline(),
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            return new PythonWorkerHealth(
                response.Status is "ok" or "healthy" or "ready",
                EmptyToNull(response.Version),
                EmptyToNull(response.Message));
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            return new PythonWorkerHealth(false, null, $"gRPC health failed: {exception.StatusCode}.");
        }
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

        for (var attempt = 0; attempt <= _options.RecoveryAttempts; attempt++)
        {
            try
            {
                var health = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                if (!health.IsHealthy)
                {
                    throw new ModelScopeException(
                        ModelScopeErrorCode.RuntimeNotInstalled,
                        health.Message ?? "Python gRPC worker is unhealthy.",
                        isRetryable: true);
                }

                var response = await _client!.LoadModelAsync(
                    new LoadModelRequest
                    {
                        ModelPath = capabilities.ModelPath,
                        ModelId = capabilities.ModelId ?? string.Empty,
                        Revision = capabilities.Revision ?? string.Empty,
                        Task = capabilities.Task ?? string.Empty,
                        AllowRemoteCode = _options.AllowRemoteCode,
                    },
                    headers: _headers,
                    deadline: Deadline(),
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.SessionId))
                {
                    throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "Python gRPC worker did not return a session ID.");
                }

                return new GrpcPythonWorkerSession(_client, response.SessionId, capabilities, Deadline, _headers);
            }
            catch (Exception exception) when (
                attempt < _options.RecoveryAttempts &&
                IsRecoverable(exception) &&
                !cancellationToken.IsCancellationRequested)
            {
                if (_supervisor is not null)
                {
                    // An unavailable health endpoint commonly means that a newly spawned
                    // process is still importing dependencies. EnsureRunning preserves a
                    // live starter and only replaces one that has exited. RPC failures
                    // after startup still require a forced restart.
                    if (exception is ModelScopeException { IsRetryable: true })
                    {
                        await _supervisor.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(_options.RecoveryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
            {
                throw MapRpcException(exception, ModelScopeErrorCode.ModelLoadFailed);
            }
        }

        throw new InvalidOperationException("Python gRPC worker recovery loop ended unexpectedly.");
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _ownedHttpClient?.Dispose();
    }

    private DateTime Deadline() => DateTime.UtcNow + _options.RequestTimeout;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsRecoverable(Exception exception) =>
        exception is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded } ||
        exception is ModelScopeException { IsRetryable: true };

    private static ModelScopeException MapRpcException(RpcException exception, ModelScopeErrorCode fallback)
    {
        var code = exception.StatusCode switch
        {
            StatusCode.NotFound => ModelScopeErrorCode.ModelNotFound,
            StatusCode.PermissionDenied when exception.Status.Detail.Contains("allow_remote_code", StringComparison.OrdinalIgnoreCase) =>
                ModelScopeErrorCode.RemoteCodeNotAllowed,
            StatusCode.PermissionDenied when exception.Status.Detail.Contains("model_path_outside_allowed_roots", StringComparison.Ordinal) =>
                ModelScopeErrorCode.InvalidRequest,
            StatusCode.PermissionDenied or StatusCode.Unauthenticated => ModelScopeErrorCode.AuthenticationRequired,
            StatusCode.ResourceExhausted => ModelScopeErrorCode.InsufficientMemory,
            StatusCode.InvalidArgument => ModelScopeErrorCode.InvalidRequest,
            StatusCode.Unimplemented => ModelScopeErrorCode.ArchitectureUnsupported,
            StatusCode.FailedPrecondition => ModelScopeErrorCode.ModelLoadFailed,
            _ => fallback,
        };
        return new ModelScopeException(
            code,
            $"Python gRPC worker returned {exception.StatusCode}.",
            exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded);
    }

    private sealed class GrpcPythonWorkerSession(
        Grpc.PythonWorker.PythonWorkerClient client,
        string sessionId,
        ModelCapabilities capabilities,
        Func<DateTime> deadline,
        Metadata? headers) : IModelSession
    {
        public ModelCapabilities Capabilities { get; } = capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                var response = await client.InvokeAsync(
                    CreateRequest(request),
                    headers: headers,
                    deadline: deadline(),
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                using var document = JsonDocument.Parse(response.OutputJson.Memory);
                return new ModelResponse(
                    document.RootElement.Clone(),
                    Capabilities.ModelId ?? Capabilities.ModelPath,
                    Capabilities.Revision ?? "local",
                    "python",
                    DateTimeOffset.UtcNow - started,
                    response.Warnings);
            }
            catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
            {
                throw MapRpcException(exception, ModelScopeErrorCode.InferenceFailed);
            }
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AsyncServerStreamingCall<StreamEvent>? call = null;
            try
            {
                call = client.InvokeStreaming(
                    CreateRequest(request),
                    headers: headers,
                    deadline: deadline(),
                    cancellationToken: cancellationToken);
                while (true)
                {
                    StreamEvent item;
                    try
                    {
                        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                        {
                            yield break;
                        }

                        item = call.ResponseStream.Current;
                    }
                    catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
                    {
                        throw MapRpcException(exception, ModelScopeErrorCode.InferenceFailed);
                    }

                    using var document = JsonDocument.Parse(item.DataJson.Memory);
                    yield return new ModelStreamEvent(
                        string.IsNullOrWhiteSpace(item.Event) ? "data" : item.Event,
                        document.RootElement.Clone(),
                        item.Terminal);
                }
            }
            finally
            {
                call?.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await client.UnloadModelAsync(
                    new UnloadModelRequest { SessionId = sessionId },
                    headers: headers,
                    deadline: deadline()).ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // Session cleanup is best effort; the supervisor owns process reclamation.
            }
        }

        private InvokeRequest CreateRequest(ModelRequest request)
        {
            var result = new InvokeRequest
            {
                SessionId = sessionId,
                Task = request.Task,
                PayloadJson = ByteString.CopyFromUtf8(request.Payload.GetRawText()),
            };
            if (request.Parameters is not null)
            {
                foreach (var parameter in request.Parameters)
                {
                    result.Parameters.Add(parameter.Key, parameter.Value);
                }
            }

            return result;
        }
    }
}
