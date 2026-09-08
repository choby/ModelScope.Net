using System.Text.Json;

namespace ModelScope.Net;

public enum ModelArtifactFormat
{
    Unknown,
    Onnx,
    Gguf,
    SafeTensors,
    PyTorch,
    TensorFlow,
    Llamafile,
    OpenVino,
    PythonCode,
}

public enum CapabilityStatus
{
    Detected,
    Compatible,
    LoadVerified,
    Certified,
    Unavailable,
    Unsupported,
}

public enum RuntimePolicyMode
{
    Development,
    Production,
}

public sealed record ModelArtifact(
    string Path,
    ModelArtifactFormat Format,
    long Size,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record RuntimeCandidate(
    string RuntimeName,
    CapabilityStatus Status,
    string Reason,
    int Priority);

public sealed record ModelCapabilities(
    string ModelPath,
    string? ModelId,
    string? Revision,
    string? Task,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<ModelArtifact> Artifacts,
    IReadOnlyList<RuntimeCandidate> RuntimeCandidates,
    bool ContainsRemoteCode,
    IReadOnlyList<string> Warnings)
{
    public ModelInspectionDiagnostics? Diagnostics { get; init; }
}

public sealed record ModelLicenseEvidence(string Source, string? Declaration, bool RequiresReview = true);
public sealed record ModelDependencyEvidence(string Name, string? VersionConstraint, string Source);
public sealed record ModelInspectionDiagnostics(
    IReadOnlyList<ModelLicenseEvidence> Licenses,
    IReadOnlyList<ModelDependencyEvidence> DeclaredDependencies,
    long ArtifactBytes,
    long? RequiredRamBytes,
    long? RequiredVramBytes,
    IReadOnlyList<string> Notes);

public sealed record ModelRequest(
    string Task,
    JsonElement Payload,
    bool Stream = false,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record ModelResponse(
    JsonElement Output,
    string ModelId,
    string Revision,
    string Runtime,
    TimeSpan Elapsed,
    IReadOnlyList<string>? Warnings = null);

public sealed record ModelStreamEvent(
    string Event,
    JsonElement Data,
    bool IsTerminal = false);

public enum ModelScopeErrorCode
{
    ModelNotFound,
    RevisionNotFound,
    AuthenticationRequired,
    LicenseAcceptanceRequired,
    DownloadIntegrityFailed,
    InsufficientDiskSpace,
    RuntimeNotInstalled,
    ArchitectureUnsupported,
    OperatorUnsupported,
    RemoteCodeNotAllowed,
    InsufficientMemory,
    ResourceQuotaExceeded,
    RemoteApiUnavailable,
    RemoteApiRateLimited,
    ModelLoadFailed,
    InferenceFailed,
    InvalidRequest,
}

public class ModelScopeException : Exception
{
    public ModelScopeException(
        ModelScopeErrorCode code,
        string message,
        bool isRetryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsRetryable = isRetryable;
    }

    public ModelScopeErrorCode Code { get; }

    public bool IsRetryable { get; }
}
