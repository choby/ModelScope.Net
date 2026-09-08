namespace ModelScope.Net.Runtime;

/// <summary>
/// Optionally decorates model sessions at the router boundary without coupling the runtime core
/// to a telemetry or hosting implementation.
/// </summary>
public interface IModelSessionInstrumentation
{
    IModelSession Instrument(
        IModelSession session,
        ModelCapabilities capabilities,
        string runtimeName);
}
