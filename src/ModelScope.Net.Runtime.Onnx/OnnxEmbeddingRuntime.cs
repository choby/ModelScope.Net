using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxEmbeddingRuntime : IModelRuntime
{
    private readonly OnnxRuntimeAdapter _runtime;
    private readonly OnnxEmbeddingOptions _options;

    public OnnxEmbeddingRuntime(OnnxRuntimeAdapter runtime, OnnxEmbeddingOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new OnnxEmbeddingOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxLength, 2);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.VocabFile);
    }

    public string Name => "onnx-embedding";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (IsTextClassificationModel(capabilities))
        {
            return new RuntimeCandidate(
                Name,
                CapabilityStatus.Unsupported,
                "The model task or architecture identifies a text classifier rather than an embedding model.",
                110);
        }

        var raw = _runtime.Evaluate(capabilities);
        if (raw.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
        {
            return new RuntimeCandidate(Name, raw.Status, raw.Reason, 110);
        }

        string vocabularyPath;
        try
        {
            vocabularyPath = ResolveModelFile(capabilities.ModelPath, _options.VocabFile);
        }
        catch (ModelScopeException exception)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, exception.Message, 110);
        }
        if (!File.Exists(vocabularyPath))
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unavailable, $"Embedding vocabulary '{_options.VocabFile}' was not found.", 110);
        }

        return new RuntimeCandidate(
            Name,
            raw.Status,
            "A WordPiece vocabulary and ONNX model are available; model-specific gold validation is still required.",
            110);
    }

    private static bool IsTextClassificationModel(ModelCapabilities capabilities)
    {
        var task = capabilities.Task?.Trim();
        return task is not null && (
                task.Equals("text-classification", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("sentiment-classification", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("sentiment-analysis", StringComparison.OrdinalIgnoreCase)) ||
            capabilities.Architectures.Any(architecture =>
                architecture.Contains("ForSequenceClassification", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var candidate = Evaluate(capabilities);
        if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
        {
            throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled, candidate.Reason);
        }

        WordPieceTokenizer tokenizer;
        try
        {
            tokenizer = WordPieceTokenizer.Load(
                ResolveModelFile(capabilities.ModelPath, _options.VocabFile),
                new WordPieceTokenizerSettings(
                    _options.MaxLength,
                    _options.Lowercase,
                    _options.PadToken,
                    _options.UnknownToken,
                    _options.ClassToken,
                    _options.SeparatorToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.ModelLoadFailed,
                $"Embedding vocabulary could not be loaded: {exception.Message}",
                isRetryable: true,
                innerException: exception);
        }
        var session = await _runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
        return new EmbeddingSession(session, tokenizer, _options);
    }

    private static string ResolveModelFile(string modelDirectory, string relativePath)
    {
        var root = Path.GetFullPath(modelDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "The embedding asset path escapes the model directory.");
        }

        return path;
    }

    private sealed class EmbeddingSession(
        IModelSession inner,
        WordPieceTokenizer tokenizer,
        OnnxEmbeddingOptions options) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
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
            var embeddings = ReadEmbeddings(raw.Output, batch.AttentionMask, options);
            var output = JsonSerializer.SerializeToElement(new
            {
                embeddings,
                dimensions = embeddings.Length == 0 ? 0 : embeddings[0].Length,
                count = embeddings.Length,
            });
            return new ModelResponse(
                output,
                raw.ModelId,
                raw.Revision,
                "onnx-embedding",
                DateTimeOffset.UtcNow - started,
                raw.Warnings);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new ModelStreamEvent("embeddings", response.Output, IsTerminal: true);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static string[] ReadTexts(JsonElement payload)
        {
            if (payload.ValueKind == JsonValueKind.String)
            {
                return [payload.GetString() ?? string.Empty];
            }

            if (payload.ValueKind != JsonValueKind.Object)
            {
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Embedding input must be a string or an object containing 'text' or 'texts'.");
            }

            if (payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return [text.GetString() ?? string.Empty];
            }

            if (payload.TryGetProperty("texts", out var texts) && texts.ValueKind == JsonValueKind.Array)
            {
                var result = texts.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String
                        ? item.GetString() ?? string.Empty
                        : throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Every embedding input must be a string.")).ToArray();
                if (result.Length > 0)
                {
                    return result;
                }
            }

            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Embedding input does not contain any text.");
        }

        private static float[][] ReadEmbeddings(
            JsonElement raw,
            long[][] attentionMask,
            OnnxEmbeddingOptions options)
        {
            if (!raw.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            {
                throw InvalidOutput("ONNX embedding output does not contain an 'outputs' object.");
            }

            string selectedName;
            JsonElement selectedValue;
            if (!string.IsNullOrWhiteSpace(options.OutputName))
            {
                if (!outputs.TryGetProperty(options.OutputName, out selectedValue))
                    throw InvalidOutput($"ONNX embedding output '{options.OutputName}' was not found.");
                selectedName = options.OutputName;
            }
            else
            {
                var selected = outputs.EnumerateObject()
                    .OrderBy(property => OutputPriority(property.Name))
                    .FirstOrDefault();
                if (selected.Value.ValueKind == JsonValueKind.Undefined)
                    throw InvalidOutput("ONNX embedding model returned no outputs.");
                selectedName = selected.Name;
                selectedValue = selected.Value;
            }

            var shape = selectedValue.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var data = selectedValue.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray();
            float[][] embeddings;
            if (shape.Length == 2)
            {
                embeddings = SliceRows(data, shape[0], shape[1]);
            }
            else if (shape.Length == 3)
            {
                embeddings = PoolTokens(data, shape, attentionMask, options.Pooling);
            }
            else
            {
                throw InvalidOutput($"Embedding output '{selectedName}' must have rank 2 or 3, but rank {shape.Length} was returned.");
            }

            if (embeddings.Length != attentionMask.Length)
                throw InvalidOutput("Embedding output batch size does not match the tokenizer batch size.");
            if (options.Normalize)
                foreach (var embedding in embeddings) Normalize(embedding);
            return embeddings;
        }

        private static int OutputPriority(string name) => name switch
        {
            "sentence_embedding" => 0,
            "pooler_output" => 1,
            "last_hidden_state" => 2,
            _ => 10,
        };

        private static float[][] SliceRows(float[] data, int rows, int columns)
        {
            if (rows < 0 || columns < 0 || (long)rows * columns != data.Length)
                throw InvalidOutput("Embedding output shape does not match its data length.");
            var result = new float[rows][];
            for (var row = 0; row < rows; row++)
                result[row] = data.AsSpan(row * columns, columns).ToArray();
            return result;
        }

        private static float[][] PoolTokens(float[] data, int[] shape, long[][] mask, OnnxEmbeddingPooling pooling)
        {
            var (batch, sequence, hidden) = (shape[0], shape[1], shape[2]);
            if (batch < 0 || sequence < 0 || hidden < 0 || (long)batch * sequence * hidden != data.Length)
                throw InvalidOutput("Token embedding output shape does not match its data length.");
            if (mask.Length != batch || mask.Any(row => row.Length != sequence))
                throw InvalidOutput("Attention mask shape does not match token embeddings.");

            var result = new float[batch][];
            for (var batchIndex = 0; batchIndex < batch; batchIndex++)
            {
                result[batchIndex] = new float[hidden];
                if (pooling == OnnxEmbeddingPooling.Cls)
                {
                    Array.Copy(data, batchIndex * sequence * hidden, result[batchIndex], 0, hidden);
                    continue;
                }

                var count = 0L;
                for (var token = 0; token < sequence; token++)
                {
                    if (mask[batchIndex][token] == 0) continue;
                    count++;
                    var offset = (batchIndex * sequence + token) * hidden;
                    for (var dimension = 0; dimension < hidden; dimension++)
                        result[batchIndex][dimension] += data[offset + dimension];
                }

                if (count == 0) throw InvalidOutput("Attention mask contains an empty sequence.");
                for (var dimension = 0; dimension < hidden; dimension++)
                    result[batchIndex][dimension] /= count;
            }

            return result;
        }

        private static void Normalize(float[] embedding)
        {
            var norm = Math.Sqrt(embedding.Sum(value => (double)value * value));
            if (norm == 0) return;
            for (var index = 0; index < embedding.Length; index++)
                embedding[index] = (float)(embedding[index] / norm);
        }

        private static ModelScopeException InvalidOutput(string message) =>
            new(ModelScopeErrorCode.InferenceFailed, message);

    }

}
