using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Onnx;

/// <summary>Deterministic greedy text generation for certified decoder-only ONNX exports with KV cache.</summary>
public sealed class OnnxTextGenerationRuntime : IModelRuntime
{
    private readonly OnnxRuntimeAdapter _runtime;
    private readonly OnnxTextGenerationOptions _options;

    public OnnxTextGenerationRuntime(OnnxTextGenerationOptions? options = null)
    {
        _options = options ?? new();
        ValidateOptions(_options);
        _runtime = new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = _options.ModelFile });
    }

    public string Name => "onnx-text-generation";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (!IsCausalLanguageModel(capabilities))
            return new(Name, CapabilityStatus.Unsupported, "The task or architecture is not a supported causal language model.", 120);
        var raw = _runtime.Evaluate(capabilities);
        if (raw.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable) return new(Name, raw.Status, raw.Reason, 120);
        try
        {
            var vocabulary = ResolveModelFile(capabilities.ModelPath, _options.VocabularyFile);
            var merges = ResolveModelFile(capabilities.ModelPath, _options.MergesFile);
            if (!File.Exists(vocabulary) || !File.Exists(merges))
                return new(Name, CapabilityStatus.Unavailable, "The byte-level BPE vocabulary or merge table was not found.", 120);
        }
        catch (ModelScopeException error) { return new(Name, CapabilityStatus.Unsupported, error.Message, 120); }
        return new(Name, raw.Status, "A decoder-only ONNX graph and byte-level BPE assets are available; fixed-model generation certification is required.", 120);
    }

    public async Task<IModelSession> CreateSessionAsync(ModelCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        var candidate = Evaluate(capabilities);
        if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
            throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled, candidate.Reason);
        try
        {
            var tokenizer = ByteLevelBpeTokenizer.Load(ResolveModelFile(capabilities.ModelPath, _options.VocabularyFile),
                ResolveModelFile(capabilities.ModelPath, _options.MergesFile));
            var inner = await _runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
            return new GenerationSession(inner, tokenizer, _options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, $"ONNX text generation assets could not be loaded: {error.Message}", true, error);
        }
    }

    private static bool IsCausalLanguageModel(ModelCapabilities capabilities) =>
        string.Equals(capabilities.Task?.Trim(), "text-generation", StringComparison.OrdinalIgnoreCase) ||
        capabilities.Architectures.Any(value => value.EndsWith("ForCausalLM", StringComparison.OrdinalIgnoreCase));

    private static void ValidateOptions(OnnxTextGenerationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.VocabularyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MergesFile); ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPromptTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DefaultMaxNewTokens, 1); ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxNewTokens, options.DefaultMaxNewTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxContextTokens, options.MaxPromptTokens + 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.EndTokenId); ArgumentOutOfRangeException.ThrowIfLessThan(options.KeyValueHeads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.HeadDimension, 1); ArgumentOutOfRangeException.ThrowIfLessThan(options.LayerCount, 1);
    }

    private static string ResolveModelFile(string directory, string relativePath)
    {
        var root = Path.GetFullPath(directory); var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "The text generation asset path escapes the model directory.");
        return path;
    }

    private sealed class GenerationSession(IModelSession inner, ByteLevelBpeTokenizer tokenizer, OnnxTextGenerationOptions options) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var input = ReadInput(request); var started = DateTimeOffset.UtcNow; var tokens = new List<int>();
            string finish;
            await foreach (var step in Generate(input.Prompt, input.MaxNewTokens, cancellationToken).ConfigureAwait(false))
            { tokens.Add(step.TokenId); if (step.Finished) break; }
            finish = tokens.Count > 0 && tokens[^1] == options.EndTokenId ? "eos" : "length";
            var completion = tokenizer.Decode(tokens.Where(token => token != options.EndTokenId));
            var output = JsonSerializer.SerializeToElement(new { text = input.Prompt + completion, generatedText = completion,
                tokenIds = tokens, promptTokens = tokenizer.Encode(input.Prompt).Length, completionTokens = tokens.Count, finishReason = finish });
            return new(output, Capabilities.ModelId ?? Capabilities.ModelPath, Capabilities.Revision ?? "local", "onnx-text-generation",
                DateTimeOffset.UtcNow - started);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = ReadInput(request); var index = 0; var decoder = new UTF8Encoding(false, false).GetDecoder();
            await foreach (var step in Generate(input.Prompt, input.MaxNewTokens, cancellationToken).ConfigureAwait(false))
            {
                var bytes = step.TokenId == options.EndTokenId ? Array.Empty<byte>() : tokenizer.DecodeTokenBytes(step.TokenId);
                var characters = new char[Math.Max(1, bytes.Length)];
                var flush = step.Finished || index + 1 == input.MaxNewTokens;
                decoder.Convert(bytes, characters, flush, out _, out var used, out _);
                var text = new string(characters, 0, used);
                yield return new("data", JsonSerializer.SerializeToElement(new { text, tokenId = step.TokenId, index = index++ }));
                if (step.Finished) break;
            }
            yield return new("done", JsonSerializer.SerializeToElement<object?>(null), true);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async IAsyncEnumerable<GenerationStep> Generate(string prompt, int maximum,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var promptIds = tokenizer.Encode(prompt);
            if (promptIds.Length == 0 || promptIds.Length > options.MaxPromptTokens || promptIds.Length + maximum > options.MaxContextTokens)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Text generation prompt or requested output exceeds the token limits.");
            var current = promptIds.Select(id => (long)id).ToArray(); var pastLength = 0;
            Dictionary<string, JsonElement>? cache = null;
            for (var generated = 0; generated < maximum; generated++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputs = new Dictionary<string, object>
                {
                    ["input_ids"] = new { shape = new[] { 1, current.Length }, data = current },
                    ["attention_mask"] = new { shape = new[] { 1, pastLength + current.Length }, data = Enumerable.Repeat(1L, pastLength + current.Length).ToArray() },
                    ["position_ids"] = new { shape = new[] { 1, current.Length }, data = Enumerable.Range(pastLength, current.Length).Select(value => (long)value).ToArray() },
                };
                for (var layer = 0; layer < options.LayerCount; layer++)
                    foreach (var kind in new[] { "key", "value" })
                    {
                        var inputName = $"past_key_values.{layer}.{kind}"; var outputName = $"present.{layer}.{kind}";
                        inputs[inputName] = cache is null ? new { shape = new[] { 1, options.KeyValueHeads, 0, options.HeadDimension }, data = Array.Empty<float>() } : cache[outputName];
                    }
                var raw = await inner.InvokeAsync(new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement(new { inputs })), cancellationToken).ConfigureAwait(false);
                var outputs = raw.Output.GetProperty("outputs"); var logits = outputs.GetProperty("logits");
                var shape = logits.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (shape.Length != 3 || shape[0] != 1 || shape[1] != current.Length || shape[2] <= 0)
                    throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, "ONNX causal language model returned invalid logits.");
                var values = logits.GetProperty("data").EnumerateArray().Skip((shape[1] - 1) * shape[2]).Take(shape[2]).Select(value => value.GetSingle()).ToArray();
                if (values.Length != shape[2] || values.Any(value => !float.IsFinite(value)))
                    throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, "ONNX causal language model logits are invalid.");
                var next = Enumerable.Range(0, values.Length).Aggregate((best, index) => values[index] > values[best] ? index : best);
                cache = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                for (var layer = 0; layer < options.LayerCount; layer++) foreach (var kind in new[] { "key", "value" })
                { var name = $"present.{layer}.{kind}"; cache[name] = outputs.GetProperty(name).Clone(); }
                pastLength += current.Length; current = [(long)next];
                var finished = next == options.EndTokenId; yield return new(next, finished);
                if (finished) yield break;
            }
        }

        private GenerationInput ReadInput(ModelRequest request)
        {
            if (!string.Equals(request.Task, "text-generation", StringComparison.OrdinalIgnoreCase))
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "The ONNX generation session only accepts text-generation requests.");
            if (request.Parameters is { Count: > 0 }) throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Generation parameters must be supplied in the typed payload.");
            if (request.Payload.ValueKind == JsonValueKind.String) return new(request.Payload.GetString() ?? string.Empty, options.DefaultMaxNewTokens);
            if (request.Payload.ValueKind != JsonValueKind.Object)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Text generation input must be a prompt string or object.");
            var hasPrompt = request.Payload.TryGetProperty("prompt", out var prompt);
            var hasText = request.Payload.TryGetProperty("text", out var text);
            if (hasPrompt == hasText || (hasPrompt ? prompt : text).ValueKind != JsonValueKind.String)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Text generation input must contain exactly one string field named prompt or text.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "prompt", "text", "maxNewTokens" };
            if (request.Payload.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Text generation input contains an unknown field.");
            var count = options.DefaultMaxNewTokens;
            if (request.Payload.TryGetProperty("maxNewTokens", out var value))
            {
                if (!value.TryGetInt32(out count)) throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "maxNewTokens must be an integer.");
            }
            if (count < 1 || count > options.MaxNewTokens) throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "maxNewTokens exceeds the configured limit.");
            return new((hasPrompt ? prompt : text).GetString() ?? string.Empty, count);
        }

        private sealed record GenerationInput(string Prompt, int MaxNewTokens);
        private sealed record GenerationStep(int TokenId, bool Finished);
    }
}
