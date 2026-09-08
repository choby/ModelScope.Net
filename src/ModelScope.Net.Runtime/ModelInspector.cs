using System.Text.Json;

namespace ModelScope.Net.Runtime;

public sealed record ModelInspectionLimits(int MaximumEntries = 100000, int MaximumDirectoryDepth = 64);

public sealed class ModelInspector
{
    private readonly ModelInspectionLimits _limits;

    public ModelInspector() : this(new ModelInspectionLimits()) { }

    public ModelInspector(ModelInspectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaximumDirectoryDepth);
        _limits = limits;
    }

    private static readonly string[] ConfigurationFiles =
    [
        "configuration.json",
        "config.json",
        "model_index.json",
    ];

    public async Task<ModelCapabilities> InspectAsync(
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Model directory '{root}' does not exist.");
        }

        var files = ScanFiles(root, cancellationToken).ToArray();
        var artifacts = new List<ModelArtifact>();
        var warnings = new List<string>();
        var architectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? task = null;
        string? modelId = null;
        string? revision = null;
        var containsRemoteCode = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var format = DetectFormat(file);
            if (format != ModelArtifactFormat.Unknown)
            {
                artifacts.Add(new ModelArtifact(relative, format, GetArtifactSize(file)));
            }

            containsRemoteCode |= format == ModelArtifactFormat.PythonCode ||
                string.Equals(Path.GetFileName(file), "requirements.txt", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var configurationFile in ConfigurationFiles)
        {
            var path = Path.Combine(root, configurationFile);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = await ReadJsonAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                ExtractConfiguration(document.RootElement, architectures, ref task, ref containsRemoteCode);
            }
            catch (JsonException)
            {
                containsRemoteCode = true;
                warnings.Add($"{configurationFile} could not be parsed; remote-code status is unknown and requires explicit review.");
            }
        }

        var manifestPath = Path.Combine(root, ".modelscope-net-manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var document = await ReadJsonAsync(manifestPath, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                modelId = GetModelId(document.RootElement);
                revision = GetString(document.RootElement, "resolvedRevision") ??
                    GetString(document.RootElement, "requestedRevision");
            }
            catch (JsonException)
            {
                warnings.Add("Snapshot manifest could not be parsed; model identity is unknown.");
            }
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            var inferredTasks = architectures.Select(InferArchitectureTask).ToArray();
            if (inferredTasks.Length > 0 && inferredTasks.All(value => value is not null) &&
                inferredTasks.Distinct(StringComparer.Ordinal).Count() == 1)
            {
                task = inferredTasks[0];
                warnings.Add("Task was inferred from declared architecture; verify model-specific behavior before use.");
            }
        }

        var candidates = BuildCandidates(artifacts, containsRemoteCode);
        if (artifacts.Count == 0)
        {
            warnings.Add("No recognized model artifacts were found.");
        }

        return new ModelCapabilities(
            root,
            modelId,
            revision,
            task,
            architectures.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            artifacts,
            candidates,
            containsRemoteCode,
            warnings)
        {
            Diagnostics = await ModelDiagnosticsInspector.InspectAsync(root, artifacts,
                files.Where(path => Path.GetDirectoryName(path) == root).Select(Path.GetFileName).ToArray()!,
                cancellationToken).ConfigureAwait(false)
        };
    }

    private IEnumerable<string> ScanFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > _limits.MaximumEntries)
                    throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Model inspection entry limit exceeded; no partial result was returned.");
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    // Prune known cache/control directories before descending, rather than filtering afterwards.
                    if (directory.Depth == 0 && entry.Name is ".git" or ".modelscope") continue;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Linked model directories require explicit review; inspection did not follow them.");
                    if (directory.Depth >= _limits.MaximumDirectoryDepth)
                        throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Model inspection depth limit exceeded; no partial result was returned.");
                    pending.Push((entry.FullName, directory.Depth + 1));
                }
                else yield return entry.FullName;
            }
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        // Opened stream length follows cache symlinks; bound reads as well in case the file grows.
        if (stream.Length > maximumBytes) throw MetadataTooLarge();
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, maximumBytes + 1L - content.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            content.Write(buffer, 0, count);
            if (content.Length > maximumBytes) throw MetadataTooLarge();
        }
        return JsonDocument.Parse(content.ToArray());

        static ModelScopeException MetadataTooLarge() => new(ModelScopeErrorCode.InvalidRequest,
            "Model metadata exceeds the inspection size limit; explicit review is required.");
    }

    private static string? InferArchitectureTask(string architecture)
    {
        if (architecture.EndsWith("ForSequenceClassification", StringComparison.Ordinal)) return "text-classification";
        if (architecture.EndsWith("ForImageClassification", StringComparison.Ordinal)) return "image-classification";
        if (architecture.EndsWith("ForCausalLM", StringComparison.Ordinal)) return "text-generation";
        // Generic encoders and seq2seq models can serve several different tasks; do not guess.
        return null;
    }

    private static IReadOnlyList<RuntimeCandidate> BuildCandidates(
        IReadOnlyList<ModelArtifact> artifacts,
        bool containsRemoteCode)
    {
        var candidates = new List<RuntimeCandidate>();
        if (artifacts.Any(artifact => artifact.Format == ModelArtifactFormat.Onnx))
        {
            candidates.Add(new RuntimeCandidate(
                "onnx",
                CapabilityStatus.Compatible,
                "ONNX artifacts were found; operator and task adapters still require load verification.",
                100));
        }

        if (artifacts.Any(artifact => artifact.Format == ModelArtifactFormat.Gguf))
        {
            candidates.Add(new RuntimeCandidate(
                "gguf",
                CapabilityStatus.Compatible,
                "GGUF artifacts were found; architecture support must be verified by the selected llama.cpp build.",
                90));
        }

        if (artifacts.Any(artifact => artifact.Format is
                ModelArtifactFormat.SafeTensors or
                ModelArtifactFormat.PyTorch or
                ModelArtifactFormat.TensorFlow) ||
            containsRemoteCode)
        {
            candidates.Add(new RuntimeCandidate(
                "python",
                CapabilityStatus.Detected,
                containsRemoteCode
                    ? "Python model code or dependencies were found; an approved isolated worker is required."
                    : "Python ecosystem model artifacts were found.",
                50));
        }

        return candidates;
    }

    private static void ExtractConfiguration(
        JsonElement root,
        ISet<string> architectures,
        ref string? task,
        ref bool containsRemoteCode)
    {
        task ??= GetString(root, "task");
        if (TryGet(root, "architectures", out var architectureElement) &&
            architectureElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in architectureElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    architectures.Add(item.GetString()!);
                }
            }
        }

        if (TryGet(root, "model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            var modelType = GetString(model, "type") ?? GetString(model, "model_type");
            if (!string.IsNullOrWhiteSpace(modelType))
            {
                architectures.Add(modelType);
            }
        }

        containsRemoteCode |= TryGet(root, "auto_map", out _) ||
            TryGet(root, "plugins", out _) ||
            string.Equals(GetString(root, "trust_remote_code"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static ModelArtifactFormat DetectFormat(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".onnx" => ModelArtifactFormat.Onnx,
            ".gguf" => ModelArtifactFormat.Gguf,
            ".safetensors" => ModelArtifactFormat.SafeTensors,
            ".pt" or ".pth" or ".bin" => ModelArtifactFormat.PyTorch,
            ".pb" or ".h5" => ModelArtifactFormat.TensorFlow,
            ".py" => ModelArtifactFormat.PythonCode,
            ".xml" when name.Contains("openvino", StringComparison.OrdinalIgnoreCase) => ModelArtifactFormat.OpenVino,
            _ when name.EndsWith(".llamafile", StringComparison.OrdinalIgnoreCase) => ModelArtifactFormat.Llamafile,
            _ => ModelArtifactFormat.Unknown,
        };
    }

    private static long GetArtifactSize(string path)
    {
        var file = new FileInfo(path);
        return file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target
            ? target.Length
            : file.Length;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static string? GetModelId(JsonElement manifest)
    {
        if (!TryGet(manifest, "modelId", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var owner = GetString(value, "owner");
            var name = GetString(value, "name");
            return string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name)
                ? null
                : $"{owner}/{name}";
        }

        return null;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
