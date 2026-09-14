using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ModelScope.Net.Runtime.Onnx;

/// <summary>Executes raw ONNX tensors with an explicitly selected execution provider.</summary>
public sealed class OnnxRuntimeAdapter : IModelRuntime
{
    private readonly OnnxRuntimeOptions _options;
    private readonly IOnnxExecutionProviderEnvironment _providerEnvironment;

    public OnnxRuntimeAdapter(OnnxRuntimeOptions? options = null)
        : this(options, new OnnxExecutionProviderEnvironment())
    {
    }

    internal OnnxRuntimeAdapter(
        OnnxRuntimeOptions? options,
        IOnnxExecutionProviderEnvironment providerEnvironment)
    {
        _options = CopyOptions(options ?? new OnnxRuntimeOptions());
        _providerEnvironment = providerEnvironment ?? throw new ArgumentNullException(nameof(providerEnvironment));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxInputElements, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.IntraOpNumThreads);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.InterOpNumThreads);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.DeviceId);
        if (_options.ExecutionProvider is OnnxExecutionProvider.Cpu or OnnxExecutionProvider.CoreML && _options.DeviceId != 0)
        {
            throw new ArgumentException(
                $"DeviceId must be 0 for the {_options.ExecutionProvider} execution provider.",
                nameof(options));
        }

        if (_options.GpuMemoryLimitBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "GpuMemoryLimitBytes must be positive when specified.");
        }

        if (_options.GpuMemoryLimitBytes is not null && _options.ExecutionProvider != OnnxExecutionProvider.Cuda)
        {
            throw new ArgumentException(
                "GpuMemoryLimitBytes is supported only by the CUDA execution provider.",
                nameof(options));
        }
    }

    public string Name => "onnx";

    /// <summary>Returns the provider that will be used, or throws when the requested native provider is unavailable.</summary>
    public OnnxExecutionProviderSelection GetExecutionProviderSelection() => ResolveExecutionProvider();

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        var artifact = SelectArtifact(capabilities);
        if (artifact is null)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, "No matching ONNX artifact was found.", 100);
        }

        string path;
        try
        {
            path = ResolveArtifactPath(capabilities.ModelPath, artifact.Path);
        }
        catch (ModelScopeException exception)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, exception.Message, 100);
        }

        if (!File.Exists(path))
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unavailable, $"ONNX artifact '{artifact.Path}' does not exist.", 100);
        }

        OnnxExecutionProviderSelection selection;
        try
        {
            selection = ResolveExecutionProvider();
        }
        catch (ModelScopeException exception) when (exception.Code == ModelScopeErrorCode.RuntimeNotInstalled)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unavailable, exception.Message, 100);
        }

        var certified = !selection.UsedCpuFallback
            && selection.Effective == OnnxExecutionProvider.Cpu
            && capabilities.ModelId is not null
            && _options.CertifiedModels.Contains(capabilities.ModelId);
        var providerDescription = $"ONNX {selection.EffectiveName}";
        return new RuntimeCandidate(
            Name,
            certified ? CapabilityStatus.Certified : CapabilityStatus.Compatible,
            certified
                ? $"The model is certified for the {providerDescription} runtime."
                : selection.UsedCpuFallback
                    ? $"{CreateCpuFallbackWarning(selection)} The result is not certified for the requested provider."
                    : _options.AllowCpuFallback && selection.Requested != OnnxExecutionProvider.Cpu
                        ? $"The model can use the {providerDescription} runtime, but CPU fallback is enabled and per-node assignment is not verified."
                    : $"The model must pass load and task-adapter verification for the {providerDescription} runtime.",
            100)
        {
            CertificationProperties = CreateCertificationProperties(selection),
        };
    }

    public Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Evaluate(capabilities);
        if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
        {
            if (candidate.Status == CapabilityStatus.Unavailable)
            {
                // Resolve again so provider failures retain their stable public error code. If the
                // provider is available, the unavailable candidate came from the model artifact.
                _ = ResolveExecutionProvider();
            }

            throw new ModelScopeException(
                candidate.Status == CapabilityStatus.Unavailable
                    ? ModelScopeErrorCode.ModelNotFound
                    : ModelScopeErrorCode.ArchitectureUnsupported,
                candidate.Reason);
        }

        var artifact = SelectArtifact(capabilities)!;
        var modelPath = ResolveArtifactPath(capabilities.ModelPath, artifact.Path);
        var selection = ResolveExecutionProvider();
        var warnings = CreateProviderWarnings(selection);
        try
        {
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            if (_options.IntraOpNumThreads > 0) sessionOptions.IntraOpNumThreads = _options.IntraOpNumThreads;
            if (_options.InterOpNumThreads > 0) sessionOptions.InterOpNumThreads = _options.InterOpNumThreads;
            _providerEnvironment.Configure(
                sessionOptions,
                selection.Effective,
                selection.DeviceId,
                _options.GpuMemoryLimitBytes,
                _options.AllowCpuFallback);

            try
            {
                IModelSession session = new OnnxModelSession(
                    new InferenceSession(modelPath, sessionOptions),
                    capabilities,
                    _options.MaxConcurrentRuns,
                    _options.MaxInputElements,
                    warnings);
                return Task.FromResult(session);
            }
            catch (OnnxRuntimeException exception)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"ONNX Runtime could not load '{artifact.Path}': {exception.Message}",
                    isRetryable: true,
                    innerException: exception);
            }
        }
        catch (Exception exception) when (IsRuntimeAvailabilityException(exception))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.RuntimeNotInstalled,
                $"ONNX execution provider '{selection.EffectiveName}' could not be initialized: {exception.Message}",
                innerException: exception);
        }
    }

    private OnnxExecutionProviderSelection ResolveExecutionProvider()
    {
        IReadOnlyList<string> availableProviders;
        string runtimeVersion;
        try
        {
            availableProviders = Array.AsReadOnly(_providerEnvironment.GetAvailableProviders()
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .ToArray());
            runtimeVersion = _providerEnvironment.GetRuntimeVersion();
        }
        catch (Exception exception) when (IsRuntimeAvailabilityException(exception))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.RuntimeNotInstalled,
                $"ONNX Runtime provider discovery failed: {exception.Message}",
                innerException: exception);
        }

        var requestedName = OnnxExecutionProviderEnvironment.GetNativeName(_options.ExecutionProvider);
        if (availableProviders.Contains(requestedName, StringComparer.OrdinalIgnoreCase))
        {
            return new OnnxExecutionProviderSelection(
                _options.ExecutionProvider,
                _options.ExecutionProvider,
                _options.DeviceId,
                UsedCpuFallback: false,
                availableProviders)
            {
                RequestedDeviceId = _options.DeviceId,
                RuntimeVersion = runtimeVersion,
            };
        }

        var cpuName = OnnxExecutionProviderEnvironment.GetNativeName(OnnxExecutionProvider.Cpu);
        if (_options.ExecutionProvider != OnnxExecutionProvider.Cpu
            && _options.AllowCpuFallback
            && availableProviders.Contains(cpuName, StringComparer.OrdinalIgnoreCase))
        {
            return new OnnxExecutionProviderSelection(
                _options.ExecutionProvider,
                OnnxExecutionProvider.Cpu,
                DeviceId: 0,
                UsedCpuFallback: true,
                availableProviders)
            {
                RequestedDeviceId = _options.DeviceId,
                RuntimeVersion = runtimeVersion,
            };
        }

        var available = availableProviders.Count == 0 ? "none" : string.Join(", ", availableProviders);
        throw new ModelScopeException(
            ModelScopeErrorCode.RuntimeNotInstalled,
            $"Requested ONNX execution provider '{requestedName}' is unavailable. Available providers: {available}."
            + (_options.ExecutionProvider == OnnxExecutionProvider.Cpu
                ? string.Empty
                : " Set AllowCpuFallback=true to explicitly permit CPU fallback."));
    }

    private static string CreateCpuFallbackWarning(OnnxExecutionProviderSelection selection) =>
        $"Requested ONNX execution provider '{OnnxExecutionProviderEnvironment.GetNativeName(selection.Requested)}' is unavailable; "
        + $"'{selection.EffectiveName}' is being used because AllowCpuFallback=true.";

    private IReadOnlyList<string> CreateProviderWarnings(OnnxExecutionProviderSelection selection)
    {
        if (selection.UsedCpuFallback)
        {
            return [CreateCpuFallbackWarning(selection)];
        }

        if (_options.AllowCpuFallback && selection.Requested != OnnxExecutionProvider.Cpu)
        {
            return
            [
                $"CPU fallback is enabled for requested ONNX execution provider '{selection.EffectiveName}'; "
                + "per-node provider assignment is not verified by this response."
            ];
        }

        return [];
    }

    private IReadOnlyDictionary<string, string> CreateCertificationProperties(
        OnnxExecutionProviderSelection selection) => new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["onnx.requestedExecutionProvider"] = selection.Requested.ToString(),
            ["onnx.effectiveExecutionProvider"] = selection.Effective.ToString(),
            ["onnx.requestedDeviceId"] = selection.RequestedDeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["onnx.effectiveDeviceId"] = selection.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["onnx.runtimeVersion"] = selection.RuntimeVersion,
            ["onnx.allowCpuFallback"] = _options.AllowCpuFallback ? "true" : "false",
            ["onnx.usedCpuFallback"] = selection.UsedCpuFallback ? "true" : "false",
        });

    private static OnnxRuntimeOptions CopyOptions(OnnxRuntimeOptions source)
    {
        var copy = new OnnxRuntimeOptions
        {
            ModelFile = source.ModelFile,
            ExecutionProvider = source.ExecutionProvider,
            DeviceId = source.DeviceId,
            AllowCpuFallback = source.AllowCpuFallback,
            GpuMemoryLimitBytes = source.GpuMemoryLimitBytes,
            IntraOpNumThreads = source.IntraOpNumThreads,
            InterOpNumThreads = source.InterOpNumThreads,
            MaxConcurrentRuns = source.MaxConcurrentRuns,
            MaxInputElements = source.MaxInputElements,
        };
        foreach (var modelId in source.CertifiedModels)
        {
            copy.CertifiedModels.Add(modelId);
        }

        return copy;
    }

    private static bool IsRuntimeAvailabilityException(Exception exception) => exception is
        OnnxRuntimeException
        or DllNotFoundException
        or EntryPointNotFoundException
        or BadImageFormatException
        or TypeInitializationException;

    private ModelArtifact? SelectArtifact(ModelCapabilities capabilities)
    {
        var artifacts = capabilities.Artifacts
            .Where(artifact => artifact.Format == ModelArtifactFormat.Onnx)
            .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(_options.ModelFile))
        {
            return artifacts.FirstOrDefault(artifact =>
                    string.Equals(Path.GetFileName(artifact.Path), "model.onnx", StringComparison.OrdinalIgnoreCase))
                ?? artifacts.FirstOrDefault();
        }

        var requested = _options.ModelFile.Replace('\\', '/');
        return artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Path.Replace('\\', '/'), requested, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveArtifactPath(string modelDirectory, string artifactPath)
    {
        var root = Path.GetFullPath(modelDirectory);
        var path = Path.GetFullPath(Path.Combine(root, artifactPath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "The ONNX artifact path escapes the model directory.");
        }

        return path;
    }

    private sealed class OnnxModelSession : IModelSession
    {
        private readonly InferenceSession _session;
        private readonly SemaphoreSlim _runGate;
        private readonly int _maxInputElements;
        private readonly IReadOnlyList<string> _warnings;
        private bool _disposed;

        public OnnxModelSession(
            InferenceSession session,
            ModelCapabilities capabilities,
            int maxConcurrentRuns,
            int maxInputElements,
            IReadOnlyList<string> warnings)
        {
            _session = session;
            Capabilities = capabilities;
            _runGate = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
            _maxInputElements = maxInputElements;
            _warnings = warnings;
        }

        public ModelCapabilities Capabilities { get; }

        public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var started = DateTimeOffset.UtcNow;
            try
            {
                var output = await Task.Run(() => Run(request.Payload), cancellationToken).ConfigureAwait(false);
                return new ModelResponse(
                    output,
                    Capabilities.ModelId ?? Capabilities.ModelPath,
                    Capabilities.Revision ?? "local",
                    "onnx",
                    DateTimeOffset.UtcNow - started,
                    _warnings);
            }
            catch (OnnxRuntimeException exception)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InferenceFailed,
                    $"ONNX inference failed: {exception.Message}",
                    innerException: exception);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Invalid ONNX tensor input: {exception.Message}",
                    innerException: exception);
            }
            finally
            {
                _runGate.Release();
            }
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new ModelStreamEvent("result", response.Output, IsTerminal: true, response.Warnings);
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _session.Dispose();
                _runGate.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        private JsonElement Run(JsonElement payload)
        {
            var inputObject = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("inputs", out var inputs)
                ? inputs
                : payload;
            if (inputObject.ValueKind != JsonValueKind.Object)
            {
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "ONNX payload must be an object or contain an 'inputs' object.");
            }

            var values = new List<NamedOnnxValue>(_session.InputMetadata.Count);
            foreach (var input in _session.InputMetadata)
            {
                if (!inputObject.TryGetProperty(input.Key, out var value))
                {
                    throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, $"Required ONNX input '{input.Key}' is missing.");
                }

                values.Add(CreateInput(input.Key, input.Value, value, _maxInputElements));
            }

            using var results = _session.Run(values);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("outputs");
                writer.WriteStartObject();
                foreach (var result in results)
                {
                    writer.WritePropertyName(result.Name);
                    WriteTensor(writer, result, _session.OutputMetadata[result.Name]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        private static NamedOnnxValue CreateInput(string name, NodeMetadata metadata, JsonElement value, int maxElements)
        {
            if (!metadata.IsTensor)
            {
                throw new ModelScopeException(ModelScopeErrorCode.OperatorUnsupported, $"ONNX input '{name}' is not a tensor.");
            }

            var dataElement = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("data", out var data) ? data : value;
            var elements = new List<JsonElement>();
            var inferredShape = Flatten(dataElement, elements, maxElements);
            var shape = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("shape", out var shapeElement)
                ? ReadShape(shapeElement)
                : inferredShape;
            ValidateShape(name, shape, metadata.Dimensions, elements.Count, maxElements);

            var type = metadata.ElementType;
            if (type == typeof(float)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(elements.Select(item => item.GetSingle()).ToArray(), shape));
            if (type == typeof(double)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<double>(elements.Select(item => item.GetDouble()).ToArray(), shape));
            if (type == typeof(long)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(elements.Select(item => item.GetInt64()).ToArray(), shape));
            if (type == typeof(int)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(elements.Select(item => item.GetInt32()).ToArray(), shape));
            if (type == typeof(bool)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>(elements.Select(item => item.GetBoolean()).ToArray(), shape));
            if (type == typeof(string)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<string>(elements.Select(item => item.GetString() ?? string.Empty).ToArray(), shape));
            throw new ModelScopeException(ModelScopeErrorCode.OperatorUnsupported, $"ONNX input '{name}' uses unsupported tensor type '{type.Name}'.");
        }

        private static int[] Flatten(JsonElement element, ICollection<JsonElement> output, int maxElements)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                if (output.Count >= maxElements)
                    throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, $"ONNX input exceeds the {maxElements} element limit.");
                output.Add(element);
                return [];
            }

            var items = element.EnumerateArray().ToArray();
            if (items.Length == 0) return [0];
            var childShape = Flatten(items[0], output, maxElements);
            for (var index = 1; index < items.Length; index++)
            {
                var currentShape = Flatten(items[index], output, maxElements);
                if (!currentShape.SequenceEqual(childShape))
                    throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "ONNX tensor arrays must be rectangular.");
            }

            return [items.Length, .. childShape];
        }

        private static int[] ReadShape(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "ONNX tensor shape must be an integer array.");
            return element.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        }

        private static void ValidateShape(string name, int[] actual, int[] expected, int elementCount, int maxElements)
        {
            if (actual.Any(dimension => dimension < 0))
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, $"ONNX input '{name}' contains a negative dimension.");
            long product = actual.Aggregate(1L, (current, dimension) => checked(current * dimension));
            if (product != elementCount || product > maxElements)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, $"ONNX input '{name}' shape does not match its data length.");
            if (expected.Length != actual.Length || expected.Where((dimension, index) => dimension > 0 && dimension != actual[index]).Any())
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"ONNX input '{name}' shape [{string.Join(',', actual)}] does not match model shape [{string.Join(',', expected)}].");
            }
        }

        private static void WriteTensor(Utf8JsonWriter writer, DisposableNamedOnnxValue value, NodeMetadata metadata)
        {
            writer.WriteStartObject();
            writer.WriteString("type", metadata.ElementType.Name);
            var dimensions = GetDimensions(value, metadata.ElementType);
            writer.WritePropertyName("shape");
            writer.WriteStartArray();
            foreach (var dimension in dimensions) writer.WriteNumberValue(dimension);
            writer.WriteEndArray();
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            if (metadata.ElementType == typeof(float)) foreach (var item in value.AsTensor<float>()) writer.WriteNumberValue(item);
            else if (metadata.ElementType == typeof(double)) foreach (var item in value.AsTensor<double>()) writer.WriteNumberValue(item);
            else if (metadata.ElementType == typeof(long)) foreach (var item in value.AsTensor<long>()) writer.WriteNumberValue(item);
            else if (metadata.ElementType == typeof(int)) foreach (var item in value.AsTensor<int>()) writer.WriteNumberValue(item);
            else if (metadata.ElementType == typeof(bool)) foreach (var item in value.AsTensor<bool>()) writer.WriteBooleanValue(item);
            else if (metadata.ElementType == typeof(string)) foreach (var item in value.AsTensor<string>()) writer.WriteStringValue(item);
            else throw new ModelScopeException(ModelScopeErrorCode.OperatorUnsupported, $"ONNX output '{value.Name}' uses unsupported tensor type '{metadata.ElementType.Name}'.");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static IReadOnlyList<int> GetDimensions(DisposableNamedOnnxValue value, Type type)
        {
            if (type == typeof(float)) return value.AsTensor<float>().Dimensions.ToArray();
            if (type == typeof(double)) return value.AsTensor<double>().Dimensions.ToArray();
            if (type == typeof(long)) return value.AsTensor<long>().Dimensions.ToArray();
            if (type == typeof(int)) return value.AsTensor<int>().Dimensions.ToArray();
            if (type == typeof(bool)) return value.AsTensor<bool>().Dimensions.ToArray();
            if (type == typeof(string)) return value.AsTensor<string>().Dimensions.ToArray();
            throw new ModelScopeException(ModelScopeErrorCode.OperatorUnsupported, $"Unsupported ONNX output tensor type '{type.Name}'.");
        }
    }
}
