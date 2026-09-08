using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxTextClassificationRuntime : IModelRuntime
{
    private readonly OnnxRuntimeAdapter _runtime;
    private readonly OnnxTextClassificationOptions _options;

    public OnnxTextClassificationRuntime(
        OnnxRuntimeAdapter runtime,
        OnnxTextClassificationOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new OnnxTextClassificationOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxLength, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.TopK);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.VocabFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ConfigFile);
    }

    public string Name => "onnx-text-classification";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (!IsTextClassificationModel(capabilities))
        {
            return new RuntimeCandidate(
                Name,
                CapabilityStatus.Unsupported,
                "The model task or architecture does not identify a supported text sequence classifier.",
                115);
        }

        var raw = _runtime.Evaluate(capabilities);
        if (raw.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
            return new RuntimeCandidate(Name, raw.Status, raw.Reason, 115);

        string vocabularyPath;
        try
        {
            vocabularyPath = ResolveModelFile(capabilities.ModelPath, _options.VocabFile);
            _ = ResolveModelFile(capabilities.ModelPath, _options.ConfigFile);
        }
        catch (ModelScopeException exception)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, exception.Message, 115);
        }

        if (!File.Exists(vocabularyPath))
        {
            return new RuntimeCandidate(
                "onnx-text-classification",
                CapabilityStatus.Unavailable,
                $"Text classification vocabulary '{_options.VocabFile}' was not found.",
                115);
        }

        return new RuntimeCandidate(
            Name,
            raw.Status,
            "A WordPiece vocabulary and ONNX model are available; label mapping and model-specific gold validation are required.",
            115);
    }

    private static bool IsTextClassificationModel(ModelCapabilities capabilities)
    {
        var task = capabilities.Task?.Trim();
        if (task is not null && task.Equals("text-classification", StringComparison.OrdinalIgnoreCase) ||
            task is not null && task.Equals("sentiment-classification", StringComparison.OrdinalIgnoreCase) ||
            task is not null && task.Equals("sentiment-analysis", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return capabilities.Architectures.Any(architecture =>
            architecture.Contains("ForSequenceClassification", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var candidate = Evaluate(capabilities);
        if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
            throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled, candidate.Reason);

        try
        {
            var tokenizer = WordPieceTokenizer.Load(
                ResolveModelFile(capabilities.ModelPath, _options.VocabFile),
                new WordPieceTokenizerSettings(
                    _options.MaxLength,
                    _options.Lowercase,
                    _options.PadToken,
                    _options.UnknownToken,
                    _options.ClassToken,
                    _options.SeparatorToken));
            var labels = await LoadLabelsAsync(
                ResolveModelFile(capabilities.ModelPath, _options.ConfigFile),
                cancellationToken).ConfigureAwait(false);
            var session = await _runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
            return new ClassificationSession(session, tokenizer, labels, _options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.ModelLoadFailed,
                $"Text classification assets could not be loaded: {exception.Message}",
                isRetryable: true,
                innerException: exception);
        }
    }

    private static async Task<IReadOnlyDictionary<int, string>> LoadLabelsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new Dictionary<int, string>();
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var result = new Dictionary<int, string>();
        if (root.TryGetProperty("id2label", out var id2label) && id2label.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in id2label.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var index) && property.Value.ValueKind == JsonValueKind.String)
                    result[index] = property.Value.GetString()!;
            }
        }
        else if (root.TryGetProperty("label2id", out var label2id) && label2id.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in label2id.EnumerateObject())
            {
                if (property.Value.TryGetInt32(out var index)) result[index] = property.Name;
            }
        }
        return result;
    }

    private static string ResolveModelFile(string modelDirectory, string relativePath)
    {
        var root = Path.GetFullPath(modelDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "The text classification asset path escapes the model directory.");
        }
        return path;
    }

    private sealed class ClassificationSession(
        IModelSession inner,
        WordPieceTokenizer tokenizer,
        IReadOnlyDictionary<int, string> configuredLabels,
        OnnxTextClassificationOptions options) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var texts = ReadTexts(request.Payload);
            var batch = tokenizer.Encode(texts);
            var tensorPayload = JsonSerializer.SerializeToElement(new
            {
                inputs = new
                {
                    input_ids = batch.InputIds,
                    attention_mask = batch.AttentionMask,
                    token_type_ids = batch.TokenTypeIds,
                },
            });
            var started = DateTimeOffset.UtcNow;
            var raw = await inner.InvokeAsync(
                new ModelRequest("raw-onnx", tensorPayload),
                cancellationToken).ConfigureAwait(false);
            var logits = ReadLogits(raw.Output, texts.Length, options.OutputName);
            var labels = Enumerable.Range(0, logits[0].Length)
                .Select(index => configuredLabels.TryGetValue(index, out var label) ? label : $"LABEL_{index}")
                .ToArray();
            var predictions = logits.Select(row => CreatePrediction(row, labels, options)).ToArray();
            var output = JsonSerializer.SerializeToElement(new
            {
                predictions,
                logits,
                count = predictions.Length,
                labelCount = labels.Length,
            });
            return new ModelResponse(
                output,
                raw.ModelId,
                raw.Revision,
                "onnx-text-classification",
                DateTimeOffset.UtcNow - started,
                raw.Warnings);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new ModelStreamEvent("classifications", response.Output, IsTerminal: true);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static object CreatePrediction(
            float[] logits,
            IReadOnlyList<string> labels,
            OnnxTextClassificationOptions options)
        {
            var probabilities = options.ScoreMode == OnnxClassificationScoreMode.Sigmoid
                ? Sigmoid(logits)
                : Softmax(logits);
            var ranked = probabilities
                .Select((score, index) => new { index, label = labels[index], score })
                .OrderByDescending(item => item.score)
                .ThenBy(item => item.index)
                .ToArray();
            var visible = options.TopK > 0 ? ranked.Take(options.TopK).ToArray() : ranked;
            var best = ranked[0];
            return new { best.index, best.label, best.score, scores = visible };
        }

        private static float[][] ReadLogits(JsonElement raw, int expectedBatch, string? outputName)
        {
            if (!raw.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
                throw InvalidOutput("ONNX classification output does not contain an 'outputs' object.");

            JsonElement selected;
            string selectedName;
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                if (!outputs.TryGetProperty(outputName, out selected))
                    throw InvalidOutput($"ONNX classification output '{outputName}' was not found.");
                selectedName = outputName;
            }
            else if (outputs.TryGetProperty("logits", out selected))
            {
                selectedName = "logits";
            }
            else
            {
                var first = outputs.EnumerateObject().FirstOrDefault();
                if (first.Value.ValueKind == JsonValueKind.Undefined)
                    throw InvalidOutput("ONNX classification model returned no outputs.");
                selectedName = first.Name;
                selected = first.Value;
            }

            var shape = selected.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var data = selected.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (shape.Length != 2 || shape[0] != expectedBatch || shape[1] <= 0 ||
                (long)shape[0] * shape[1] != data.Length)
            {
                throw InvalidOutput(
                    $"Classification output '{selectedName}' must have shape [batch, labels] matching the request.");
            }

            var result = new float[shape[0]][];
            for (var row = 0; row < shape[0]; row++)
                result[row] = data.AsSpan(row * shape[1], shape[1]).ToArray();
            return result;
        }

        private static float[] Softmax(IReadOnlyList<float> logits)
        {
            var maximum = logits.Max();
            var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
            var sum = exponentials.Sum();
            return exponentials.Select(value => (float)(value / sum)).ToArray();
        }

        private static float[] Sigmoid(IReadOnlyList<float> logits) => logits
            .Select(value => (float)(1d / (1d + Math.Exp(-value))))
            .ToArray();

        private static string[] ReadTexts(JsonElement payload)
        {
            if (payload.ValueKind == JsonValueKind.String)
                return [payload.GetString() ?? string.Empty];
            if (payload.ValueKind != JsonValueKind.Object)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "Text classification input must be a string or an object containing 'text' or 'texts'.");
            }
            if (payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return [text.GetString() ?? string.Empty];
            if (payload.TryGetProperty("texts", out var texts) && texts.ValueKind == JsonValueKind.Array)
            {
                var result = texts.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String
                        ? item.GetString() ?? string.Empty
                        : throw new ModelScopeException(
                            ModelScopeErrorCode.InvalidRequest,
                            "Every text classification input must be a string.")).ToArray();
                if (result.Length > 0) return result;
            }
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "Text classification input does not contain any text.");
        }

        private static ModelScopeException InvalidOutput(string message) =>
            new(ModelScopeErrorCode.InferenceFailed, message);
    }
}
