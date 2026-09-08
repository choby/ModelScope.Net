using System.Runtime.CompilerServices;
using System.Text.Json;
using SkiaSharp;

namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxImageClassificationRuntime : IModelRuntime
{
    private readonly OnnxRuntimeAdapter _runtime;
    private readonly OnnxImageClassificationOptions _options;

    public OnnxImageClassificationRuntime(
        OnnxRuntimeAdapter runtime,
        OnnxImageClassificationOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new OnnxImageClassificationOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ConfigFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.PreprocessorConfigFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.TopK, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxEncodedImageBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxSourcePixels, 1);
    }

    public string Name => "onnx-image-classification";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (!IsImageClassificationModel(capabilities))
        {
            return new RuntimeCandidate(
                Name,
                CapabilityStatus.Unsupported,
                "The model task or architecture does not identify a supported image classifier.",
                115);
        }

        var raw = _runtime.Evaluate(capabilities);
        if (raw.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
            return new RuntimeCandidate(Name, raw.Status, raw.Reason, 115);

        try
        {
            var processorPath = ResolveModelFile(capabilities.ModelPath, _options.PreprocessorConfigFile);
            if (!File.Exists(processorPath))
            {
                return new RuntimeCandidate(
                    Name,
                    CapabilityStatus.Unavailable,
                    $"Image preprocessor configuration '{_options.PreprocessorConfigFile}' was not found.",
                    115);
            }
            _ = ResolveModelFile(capabilities.ModelPath, _options.ConfigFile);
        }
        catch (ModelScopeException exception)
        {
            return new RuntimeCandidate(Name, CapabilityStatus.Unsupported, exception.Message, 115);
        }

        return new RuntimeCandidate(
            Name,
            raw.Status,
            "An ONNX image classifier and supported preprocessor configuration are available; model-specific gold validation is required.",
            115);
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
            var preprocessor = await ImagePreprocessor.LoadAsync(
                ResolveModelFile(capabilities.ModelPath, _options.PreprocessorConfigFile),
                _options,
                cancellationToken).ConfigureAwait(false);
            var labels = await LoadLabelsAsync(
                ResolveModelFile(capabilities.ModelPath, _options.ConfigFile),
                cancellationToken).ConfigureAwait(false);
            var inner = await _runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
            return new ImageClassificationSession(inner, preprocessor, labels, _options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.ModelLoadFailed,
                $"Image classification assets could not be loaded: {exception.Message}",
                isRetryable: true,
                innerException: exception);
        }
    }

    private static bool IsImageClassificationModel(ModelCapabilities capabilities)
    {
        var task = capabilities.Task?.Trim();
        return task is not null && (
                task.Equals("image-classification", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("image-classification-imagenet", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("vision-classification", StringComparison.OrdinalIgnoreCase)) ||
            capabilities.Architectures.Any(architecture =>
                architecture.Contains("ForImageClassification", StringComparison.OrdinalIgnoreCase));
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
        var result = new Dictionary<int, string>();
        if (document.RootElement.TryGetProperty("id2label", out var labels) &&
            labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in labels.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var index) && property.Value.ValueKind == JsonValueKind.String)
                    result[index] = property.Value.GetString()!;
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
                "The image classification asset path escapes the model directory.");
        }
        return path;
    }

    private sealed class ImageClassificationSession(
        IModelSession inner,
        ImagePreprocessor preprocessor,
        IReadOnlyDictionary<int, string> configuredLabels,
        OnnxImageClassificationOptions options) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var images = ReadImages(
                request.Payload,
                options.MaxBatchSize,
                options.MaxEncodedImageBytes);
            var pixelValues = images.Select(preprocessor.Process).ToArray();
            var raw = await inner.InvokeAsync(
                new ModelRequest(
                    "raw-onnx",
                    JsonSerializer.SerializeToElement(new { inputs = new { pixel_values = pixelValues } })),
                cancellationToken).ConfigureAwait(false);
            var logits = ReadLogits(raw.Output, images.Length, options.OutputName);
            var labels = Enumerable.Range(0, logits[0].Length)
                .Select(index => configuredLabels.TryGetValue(index, out var label) ? label : $"LABEL_{index}")
                .ToArray();
            var predictions = logits.Select(row => CreatePrediction(row, labels, options.TopK)).ToArray();
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
                "onnx-image-classification",
                raw.Elapsed,
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

        private static object CreatePrediction(float[] logits, IReadOnlyList<string> labels, int topK)
        {
            var maximum = logits.Max();
            var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
            var sum = exponentials.Sum();
            var ranked = exponentials
                .Select((value, index) => new { index, label = labels[index], score = (float)(value / sum) })
                .OrderByDescending(item => item.score)
                .ThenBy(item => item.index)
                .ToArray();
            var best = ranked[0];
            return new { best.index, best.label, best.score, scores = ranked.Take(topK).ToArray() };
        }

        private static float[][] ReadLogits(JsonElement raw, int expectedBatch, string? outputName)
        {
            if (!raw.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
                throw InvalidOutput("ONNX image classification output does not contain an 'outputs' object.");
            JsonElement selected;
            string selectedName;
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                if (!outputs.TryGetProperty(outputName, out selected))
                    throw InvalidOutput($"ONNX image classification output '{outputName}' was not found.");
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
                    throw InvalidOutput("ONNX image classification model returned no outputs.");
                selectedName = first.Name;
                selected = first.Value;
            }
            var shape = selected.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var data = selected.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (shape.Length != 2 || shape[0] != expectedBatch || shape[1] <= 0 ||
                (long)shape[0] * shape[1] != data.Length)
            {
                throw InvalidOutput(
                    $"Image classification output '{selectedName}' must have shape [batch, labels] matching the request.");
            }
            var result = new float[shape[0]][];
            for (var row = 0; row < shape[0]; row++)
                result[row] = data.AsSpan(row * shape[1], shape[1]).ToArray();
            return result;
        }

        private static byte[][] ReadImages(
            JsonElement payload,
            int maxBatchSize,
            int maxEncodedImageBytes)
        {
            string[] encoded;
            if (payload.ValueKind == JsonValueKind.String)
            {
                encoded = [payload.GetString() ?? string.Empty];
            }
            else if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String)
            {
                encoded = [image.GetString() ?? string.Empty];
            }
            else if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                encoded = images.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String
                        ? item.GetString() ?? string.Empty
                        : throw new ModelScopeException(
                            ModelScopeErrorCode.InvalidRequest,
                            "Every image classification input must be a Base64 string.")).ToArray();
            }
            else
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "Image classification input must contain a Base64 'image' or 'images' value.");
            }

            if (encoded.Length == 0 || encoded.Length > maxBatchSize)
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Image classification batch size must be between 1 and {maxBatchSize}.");
            try
            {
                return encoded.Select(value =>
                {
                    var comma = value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? value.IndexOf(',')
                        : -1;
                    var base64 = comma >= 0 ? value[(comma + 1)..] : value;
                    var maximumBase64Characters = checked(((long)maxEncodedImageBytes + 2) / 3 * 4);
                    if (string.IsNullOrWhiteSpace(base64) || base64.Length > maximumBase64Characters)
                    {
                        throw new ModelScopeException(
                            ModelScopeErrorCode.InvalidRequest,
                            $"Encoded image size must be between 1 and {maxEncodedImageBytes} bytes.");
                    }
                    var decoded = Convert.FromBase64String(base64);
                    if (decoded.Length > maxEncodedImageBytes)
                    {
                        throw new ModelScopeException(
                            ModelScopeErrorCode.InvalidRequest,
                            $"Encoded image size must be between 1 and {maxEncodedImageBytes} bytes.");
                    }
                    return decoded;
                }).ToArray();
            }
            catch (FormatException exception)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "Image classification input is not valid Base64.",
                    innerException: exception);
            }
        }

        private static ModelScopeException InvalidOutput(string message) =>
            new(ModelScopeErrorCode.InferenceFailed, message);
    }

    private sealed record ImagePreprocessor(
        int ResizeShortestEdge,
        int CropHeight,
        int CropWidth,
        float RescaleFactor,
        float[] Mean,
        float[] StandardDeviation,
        OnnxImageClassificationOptions Options)
    {
        public static async Task<ImagePreprocessor> LoadAsync(
            string path,
            OnnxImageClassificationOptions options,
            CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            RequireTrue(root, "do_resize");
            RequireTrue(root, "do_center_crop");
            RequireTrue(root, "do_rescale");
            RequireTrue(root, "do_normalize");
            if (root.TryGetProperty("resample", out var resample) && resample.GetInt32() != 2)
                throw new ModelScopeException(
                    ModelScopeErrorCode.OperatorUnsupported,
                    "Only PIL bilinear image resampling (resample=2) is currently supported.");
            var size = root.GetProperty("size").GetProperty("shortest_edge").GetInt32();
            var crop = root.GetProperty("crop_size");
            var mean = ReadTriplet(root.GetProperty("image_mean"), "image_mean");
            var standardDeviation = ReadTriplet(root.GetProperty("image_std"), "image_std");
            if (standardDeviation.Any(value => value == 0))
                throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "Image standard deviation cannot contain zero.");
            return new ImagePreprocessor(
                size,
                crop.GetProperty("height").GetInt32(),
                crop.GetProperty("width").GetInt32(),
                root.GetProperty("rescale_factor").GetSingle(),
                mean,
                standardDeviation,
                options);
        }

        public float[][][] Process(byte[] encoded)
        {
            if (encoded.Length == 0 || encoded.Length > Options.MaxEncodedImageBytes)
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Encoded image size must be between 1 and {Options.MaxEncodedImageBytes} bytes.");
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data) ?? throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "The image format could not be identified.");
            var info = codec.Info;
            if ((long)info.Width * info.Height > Options.MaxSourcePixels)
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Image dimensions exceed the {Options.MaxSourcePixels} pixel limit.");

            using var image = SKBitmap.Decode(encoded) ?? throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "The image could not be decoded.");
            var resizedWidth = image.Width <= image.Height
                ? ResizeShortestEdge
                : (int)((long)ResizeShortestEdge * image.Width / image.Height);
            var resizedHeight = image.Width <= image.Height
                ? (int)((long)ResizeShortestEdge * image.Height / image.Width)
                : ResizeShortestEdge;
            if (resizedWidth < CropWidth || resizedHeight < CropHeight)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Image dimensions are invalid for center cropping.");
            using var resized = new SKBitmap(new SKImageInfo(
                resizedWidth,
                resizedHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(resized))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(
                    image,
                    new SKRect(0, 0, resizedWidth, resizedHeight),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Flush();
            }
            var left = (resizedWidth - CropWidth) / 2;
            var top = (resizedHeight - CropHeight) / 2;

            var tensor = Enumerable.Range(0, 3)
                .Select(_ => Enumerable.Range(0, CropHeight).Select(_ => new float[CropWidth]).ToArray())
                .ToArray();
            for (var y = 0; y < CropHeight; y++)
            {
                for (var x = 0; x < CropWidth; x++)
                {
                    var pixel = resized.GetPixel(left + x, top + y);
                    tensor[0][y][x] = Normalize(pixel.Red, 0);
                    tensor[1][y][x] = Normalize(pixel.Green, 1);
                    tensor[2][y][x] = Normalize(pixel.Blue, 2);
                }
            }
            return tensor;
        }

        private float Normalize(byte value, int channel) =>
            (value * RescaleFactor - Mean[channel]) / StandardDeviation[channel];

        private static void RequireTrue(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.True)
                throw new ModelScopeException(
                    ModelScopeErrorCode.OperatorUnsupported,
                    $"Image preprocessing option '{name}=true' is required.");
        }

        private static float[] ReadTriplet(JsonElement element, string name)
        {
            var values = element.EnumerateArray().Select(item => item.GetSingle()).ToArray();
            return values.Length == 3
                ? values
                : throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"Image preprocessing option '{name}' must contain three channels.");
        }
    }
}
