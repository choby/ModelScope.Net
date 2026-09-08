namespace ModelScope.Net;

public interface IModelRuntime
{
    string Name { get; }

    RuntimeCandidate Evaluate(ModelCapabilities capabilities);

    Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default);
}

public interface IModelSession : IAsyncDisposable
{
    ModelCapabilities Capabilities { get; }

    Task<ModelResponse> InvokeAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Marks a runtime that sends inference data to an external service.</summary>
public interface IRemoteModelRuntime : IModelRuntime
{
}
