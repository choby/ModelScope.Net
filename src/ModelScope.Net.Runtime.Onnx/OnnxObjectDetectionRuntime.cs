using System.Runtime.CompilerServices;
using System.Text.Json;
using SkiaSharp;

namespace ModelScope.Net.Runtime.Onnx;

public sealed class OnnxObjectDetectionRuntime : IModelRuntime
{
    private readonly OnnxRuntimeAdapter _runtime;
    private readonly OnnxObjectDetectionOptions _options;

    public OnnxObjectDetectionRuntime(
        OnnxRuntimeAdapter runtime,
        OnnxObjectDetectionOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new OnnxObjectDetectionOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ConfigFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InputHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InputWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.RescaleFactor, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ConfidenceThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.ConfidenceThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.IouThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.IouThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDetections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxEncodedImageBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxSourcePixels, 1);
    }

    public string Name => "onnx-object-detection";

    public RuntimeCandidate Evaluate(ModelCapabilities capabilities)
    {
        if (!IsObjectDetectionModel(capabilities))
        {
            return new RuntimeCandidate(
                Name,
                CapabilityStatus.Unsupported,
                "The model task or architecture does not identify a supported object detector.",
                116);
        }

        var raw = _runtime.Evaluate(capabilities);
        if (raw.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable)
            return new RuntimeCandidate(Name, raw.Status, raw.Reason, 116);

        return new RuntimeCandidate(
            Name,
            raw.Status,
            "An ONNX detector is available; letterbox preprocessing and NMS still require model-specific gold validation.",
            116);
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
            var labels = await LoadLabelsAsync(
                ResolveModelFile(capabilities.ModelPath, _options.ConfigFile),
                cancellationToken).ConfigureAwait(false);
            var inner = await _runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
            return new ObjectDetectionSession(inner, labels, _options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.ModelLoadFailed,
                $"Object detection assets could not be loaded: {exception.Message}",
                isRetryable: true,
                innerException: exception);
        }
    }

    internal static bool IsObjectDetectionModel(ModelCapabilities capabilities)
    {
        var task = capabilities.Task?.Trim();
        return task is not null && (
                task.Equals("object-detection", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("image-object-detection", StringComparison.OrdinalIgnoreCase) ||
                task.Equals("domain-specific-object-detection", StringComparison.OrdinalIgnoreCase)) ||
            capabilities.Architectures.Any(architecture =>
                architecture.Contains("ForObjectDetection", StringComparison.OrdinalIgnoreCase) ||
                architecture.Contains("Yolo", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyDictionary<int, string>> LoadLabelsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Coco80Labels;
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
        return result.Count == 0 ? Coco80Labels : result;
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
                "The object detection asset path escapes the model directory.");
        }
        return path;
    }

    private sealed class ObjectDetectionSession(
        IModelSession inner,
        IReadOnlyDictionary<int, string> configuredLabels,
        OnnxObjectDetectionOptions options) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var encoded = ReadImages(request.Payload, options.MaxBatchSize, options.MaxEncodedImageBytes);
            if (encoded.Length != 1)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "The ONNX object-detection preview adapter currently accepts a single image.");
            }

            var prepared = LetterboxImage.Process(encoded[0], options);
            var tensorName = string.IsNullOrWhiteSpace(options.InputName) ? "images" : options.InputName;
            var raw = await inner.InvokeAsync(
                new ModelRequest(
                    "raw-onnx",
                    JsonSerializer.SerializeToElement(new
                    {
                        inputs = new Dictionary<string, object>
                        {
                            [tensorName] = new[] { prepared.Tensor },
                        },
                    })),
                cancellationToken).ConfigureAwait(false);
            var detections = Decode(
                raw.Output,
                prepared,
                configuredLabels,
                options)
                .Select(item => new
                {
                    index = item.Index,
                    label = item.Label,
                    score = item.Score,
                    box = item.Box,
                })
                .ToArray();
            var output = JsonSerializer.SerializeToElement(new
            {
                detections,
                boxes = detections.Select(item => item.box).ToArray(),
                scores = detections.Select(item => item.score).ToArray(),
                labels = detections.Select(item => item.label).ToArray(),
                count = detections.Length,
            });
            return new ModelResponse(
                output,
                raw.ModelId,
                raw.Revision,
                "onnx-object-detection",
                raw.Elapsed,
                raw.Warnings);
        }

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new ModelStreamEvent("detections", response.Output, IsTerminal: true);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static Detection[] Decode(
        JsonElement raw,
        LetterboxImage prepared,
        IReadOnlyDictionary<int, string> labels,
        OnnxObjectDetectionOptions options)
    {
        if (!raw.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            throw InvalidOutput("ONNX object detection output does not contain an 'outputs' object.");

        JsonElement selected;
        if (!string.IsNullOrWhiteSpace(options.OutputName))
        {
            if (!outputs.TryGetProperty(options.OutputName, out selected))
                throw InvalidOutput($"ONNX object detection output '{options.OutputName}' was not found.");
        }
        else
        {
            var first = outputs.EnumerateObject().FirstOrDefault();
            if (first.Value.ValueKind == JsonValueKind.Undefined)
                throw InvalidOutput("ONNX object detection model returned no outputs.");
            selected = first.Value;
        }

        var shape = SqueezeLeadingOnes(
            selected.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        var data = selected.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (shape.Length != 3 || shape[0] != 1 || data.Length != shape[0] * shape[1] * shape[2])
        {
            throw InvalidOutput(
                $"Object detection output must have shape [1, num, 5+classes] or [1, 4+classes, num]; received [{string.Join(',', shape)}].");
        }

        var yolov5 = shape[2] >= 6;
        var channels = yolov5 ? shape[2] : shape[1];
        var count = yolov5 ? shape[1] : shape[2];
        var classCount = yolov5 ? channels - 5 : channels - 4;
        if (classCount < 1 || count < 1)
            throw InvalidOutput("Object detection output does not contain class scores.");

        var candidates = new List<Detection>(count);
        for (var index = 0; index < count; index++)
        {
            float cx, cy, width, height, objectness;
            var classOffset = 0;
            if (yolov5)
            {
                var offset = index * channels;
                cx = data[offset];
                cy = data[offset + 1];
                width = data[offset + 2];
                height = data[offset + 3];
                objectness = data[offset + 4];
                classOffset = offset + 5;
            }
            else
            {
                cx = data[index];
                cy = data[count + index];
                width = data[2 * count + index];
                height = data[3 * count + index];
                objectness = 1f;
                classOffset = 4 * count + index;
            }

            var bestClass = 0;
            var bestClassScore = float.NegativeInfinity;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                var score = yolov5 ? data[classOffset + classIndex] : data[classOffset + classIndex * count];
                if (score > bestClassScore)
                {
                    bestClassScore = score;
                    bestClass = classIndex;
                }
            }

            var confidence = objectness * bestClassScore;
            if (confidence < options.ConfidenceThreshold) continue;
            candidates.Add(MapBox(cx, cy, width, height, bestClass, confidence, prepared, labels, options));
        }

        return NonMaxSuppression(candidates, options.IouThreshold, options.MaxDetections);
    }

    private static Detection MapBox(
        float centerX,
        float centerY,
        float width,
        float height,
        int classIndex,
        float score,
        LetterboxImage prepared,
        IReadOnlyDictionary<int, string> labels,
        OnnxObjectDetectionOptions options)
    {
        var normalized = centerX <= 1.5f && centerY <= 1.5f && width <= 1.5f && height <= 1.5f;
        if (normalized)
        {
            centerX *= options.InputWidth;
            centerY *= options.InputHeight;
            width *= options.InputWidth;
            height *= options.InputHeight;
        }

        var left = (centerX - width / 2f - prepared.PadX) / prepared.Scale;
        var top = (centerY - height / 2f - prepared.PadY) / prepared.Scale;
        var right = (centerX + width / 2f - prepared.PadX) / prepared.Scale;
        var bottom = (centerY + height / 2f - prepared.PadY) / prepared.Scale;
        left = Math.Clamp(left, 0, prepared.SourceWidth);
        top = Math.Clamp(top, 0, prepared.SourceHeight);
        right = Math.Clamp(right, 0, prepared.SourceWidth);
        bottom = Math.Clamp(bottom, 0, prepared.SourceHeight);
        var label = labels.TryGetValue(classIndex, out var name) ? name : $"LABEL_{classIndex}";
        return new Detection(
            classIndex,
            label,
            score,
            [left, top, right, bottom]);
    }

    private static int[] SqueezeLeadingOnes(int[] shape)
    {
        var start = 0;
        while (shape.Length - start > 3 && shape[start] == 1)
            start++;
        var squeezed = shape[start..];
        return squeezed.Length == 2 ? [1, ..squeezed] : squeezed;
    }

    private static Detection[] NonMaxSuppression(
        IReadOnlyList<Detection> candidates,
        float iouThreshold,
        int maxDetections)
    {
        var ordered = candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToList();
        var kept = new List<Detection>(Math.Min(ordered.Count, maxDetections));
        while (ordered.Count > 0 && kept.Count < maxDetections)
        {
            var current = ordered[0];
            kept.Add(current);
            ordered.RemoveAt(0);
            ordered.RemoveAll(item =>
                item.Index == current.Index && IntersectionOverUnion(current.Box, item.Box) > iouThreshold);
        }

        return [.. kept];
    }

    private static float IntersectionOverUnion(float[] first, float[] second)
    {
        var left = Math.Max(first[0], second[0]);
        var top = Math.Max(first[1], second[1]);
        var right = Math.Min(first[2], second[2]);
        var bottom = Math.Min(first[3], second[3]);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        var intersection = width * height;
        var union = (first[2] - first[0]) * (first[3] - first[1]) +
            (second[2] - second[0]) * (second[3] - second[1]) -
            intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static byte[][] ReadImages(JsonElement payload, int maxBatchSize, int maxEncodedImageBytes)
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
                        "Every object detection input must be a Base64 string.")).ToArray();
        }
        else
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "Object detection input must contain a Base64 'image' or 'images' value.");
        }

        if (encoded.Length == 0 || encoded.Length > maxBatchSize)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                $"Object detection batch size must be between 1 and {maxBatchSize}.");
        }

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
                "Object detection input is not valid Base64.",
                innerException: exception);
        }
    }

    private static readonly IReadOnlyDictionary<int, string> Coco80Labels =
        new Dictionary<int, string>
        {
            [0] = "person", [1] = "bicycle", [2] = "car", [3] = "motorcycle", [4] = "airplane",
            [5] = "bus", [6] = "train", [7] = "truck", [8] = "boat", [9] = "traffic light",
            [10] = "fire hydrant", [11] = "stop sign", [12] = "parking meter", [13] = "bench",
            [14] = "bird", [15] = "cat", [16] = "dog", [17] = "horse", [18] = "sheep", [19] = "cow",
            [20] = "elephant", [21] = "bear", [22] = "zebra", [23] = "giraffe", [24] = "backpack",
            [25] = "umbrella", [26] = "handbag", [27] = "tie", [28] = "suitcase", [29] = "frisbee",
            [30] = "skis", [31] = "snowboard", [32] = "sports ball", [33] = "kite", [34] = "baseball bat",
            [35] = "baseball glove", [36] = "skateboard", [37] = "surfboard", [38] = "tennis racket",
            [39] = "bottle", [40] = "wine glass", [41] = "cup", [42] = "fork", [43] = "knife",
            [44] = "spoon", [45] = "bowl", [46] = "banana", [47] = "apple", [48] = "sandwich",
            [49] = "orange", [50] = "broccoli", [51] = "carrot", [52] = "hot dog", [53] = "pizza",
            [54] = "donut", [55] = "cake", [56] = "chair", [57] = "couch", [58] = "potted plant",
            [59] = "bed", [60] = "dining table", [61] = "toilet", [62] = "tv", [63] = "laptop",
            [64] = "mouse", [65] = "remote", [66] = "keyboard", [67] = "cell phone", [68] = "microwave",
            [69] = "oven", [70] = "toaster", [71] = "sink", [72] = "refrigerator", [73] = "book",
            [74] = "clock", [75] = "vase", [76] = "scissors", [77] = "teddy bear", [78] = "hair drier",
            [79] = "toothbrush",
        };

    private static ModelScopeException InvalidOutput(string message) =>
        new(ModelScopeErrorCode.InferenceFailed, message);

    private sealed record Detection(int Index, string Label, float Score, float[] Box);

    private sealed record LetterboxImage(
        float[][][] Tensor,
        int SourceWidth,
        int SourceHeight,
        float Scale,
        float PadX,
        float PadY)
    {
        public static LetterboxImage Process(byte[] encoded, OnnxObjectDetectionOptions options)
        {
            if (encoded.Length == 0 || encoded.Length > options.MaxEncodedImageBytes)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Encoded image size must be between 1 and {options.MaxEncodedImageBytes} bytes.");
            }

            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data) ?? throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "The image format could not be identified.");
            var info = codec.Info;
            if ((long)info.Width * info.Height > options.MaxSourcePixels)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    $"Image dimensions exceed the {options.MaxSourcePixels} pixel limit.");
            }

            using var image = SKBitmap.Decode(encoded) ?? throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "The image could not be decoded.");
            var scale = Math.Min(
                (float)options.InputWidth / image.Width,
                (float)options.InputHeight / image.Height);
            var resizedWidth = Math.Max(1, (int)Math.Round(image.Width * scale, MidpointRounding.AwayFromZero));
            var resizedHeight = Math.Max(1, (int)Math.Round(image.Height * scale, MidpointRounding.AwayFromZero));
            var padX = (options.InputWidth - resizedWidth) / 2f;
            var padY = (options.InputHeight - resizedHeight) / 2f;
            using var canvasBitmap = new SKBitmap(new SKImageInfo(
                options.InputWidth,
                options.InputHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(canvasBitmap))
            {
                canvas.Clear(new SKColor(options.LetterboxPadValue, options.LetterboxPadValue, options.LetterboxPadValue));
                canvas.DrawBitmap(
                    image,
                    new SKRect(padX, padY, padX + resizedWidth, padY + resizedHeight),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Flush();
            }

            var tensor = Enumerable.Range(0, 3)
                .Select(_ => Enumerable.Range(0, options.InputHeight).Select(_ => new float[options.InputWidth]).ToArray())
                .ToArray();
            for (var y = 0; y < options.InputHeight; y++)
            {
                for (var x = 0; x < options.InputWidth; x++)
                {
                    var pixel = canvasBitmap.GetPixel(x, y);
                    tensor[0][y][x] = pixel.Red * options.RescaleFactor;
                    tensor[1][y][x] = pixel.Green * options.RescaleFactor;
                    tensor[2][y][x] = pixel.Blue * options.RescaleFactor;
                }
            }

            return new LetterboxImage(tensor, image.Width, image.Height, scale, padX, padY);
        }
    }
}
