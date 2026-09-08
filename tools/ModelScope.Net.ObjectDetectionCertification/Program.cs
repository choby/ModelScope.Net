using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Onnx;

var arguments = ParseArguments(args);
var onnxModelPath = Path.GetFullPath(Require(arguments, "--onnx-model"));
var inputsPath = Path.GetFullPath(Require(arguments, "--inputs"));
var goldPath = Path.GetFullPath(Require(arguments, "--gold"));
var outputPath = Path.GetFullPath(Require(arguments, "--output"));
var reportPath = Path.GetFullPath(Require(arguments, "--report"));
var onnxFile = arguments.GetValueOrDefault("--onnx-file", "detect/yolov5n.onnx");

using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath));
var gold = goldDocument.RootElement;
var modelId = gold.GetProperty("modelId").GetString()!;
var revision = gold.GetProperty("revision").GetString()!;
var goldSamples = gold.GetProperty("samples").EnumerateArray().ToArray();

using var inputsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(inputsPath));
var inputItems = inputsDocument.RootElement.EnumerateArray().ToArray();
if (inputItems.Length != goldSamples.Length)
    throw new InvalidOperationException("Input count does not match the Python gold file.");

var encodedImages = new string[inputItems.Length];
var inputSha256 = new string[inputItems.Length];
var names = new string[inputItems.Length];
for (var index = 0; index < inputItems.Length; index++)
{
    var relative = inputItems[index].GetProperty("path").GetString()!;
    var path = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(relative);
    var bytes = await File.ReadAllBytesAsync(path);
    encodedImages[index] = Convert.ToBase64String(bytes);
    inputSha256[index] = Convert.ToHexStringLower(SHA256.HashData(bytes));
    names[index] = inputItems[index].GetProperty("name").GetString()!;
    var expectedSha = goldSamples[index].GetProperty("sha256").GetString();
    if (!string.Equals(expectedSha, inputSha256[index], StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Input SHA-256 mismatch for '{names[index]}'.");
}

var modelOnnx = Path.Combine(onnxModelPath, onnxFile);
var capabilities = new ModelCapabilities(
    onnxModelPath,
    modelId,
    revision,
    "object-detection",
    ["YoloForObjectDetection"],
    [new ModelArtifact(onnxFile, ModelArtifactFormat.Onnx, new FileInfo(modelOnnx).Length)],
    [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "certification", 100)],
    ContainsRemoteCode: false,
    Warnings: []);
var runtime = new OnnxObjectDetectionRuntime(
    new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = onnxFile }),
    new OnnxObjectDetectionOptions
    {
        InputHeight = gold.GetProperty("inputSize").GetInt32(),
        InputWidth = gold.GetProperty("inputSize").GetInt32(),
        ConfidenceThreshold = gold.GetProperty("confidenceThreshold").GetSingle(),
        IouThreshold = gold.GetProperty("iouThreshold").GetSingle(),
    });

await using var session = await runtime.CreateSessionAsync(capabilities);
var actualSamples = new List<SampleResult>();
var matchedLabels = 0;
var comparedDetections = 0;
var minimumIou = double.PositiveInfinity;
var maximumScoreAbsoluteError = 0d;
for (var index = 0; index < encodedImages.Length; index++)
{
    var response = await session.InvokeAsync(new ModelRequest(
        "object-detection",
        JsonSerializer.SerializeToElement(new { image = encodedImages[index] })));
    var actual = response.Output.GetProperty("detections").EnumerateArray()
        .Select(ReadDetection)
        .ToArray();
    var expected = goldSamples[index].GetProperty("detections").EnumerateArray()
        .Select(ReadDetection)
        .ToArray();
    var pairs = MatchDetections(expected, actual);
    comparedDetections += pairs.Count;
    matchedLabels += pairs.Count(pair => pair.Expected.Index == pair.Actual.Index);
    foreach (var pair in pairs)
    {
        minimumIou = Math.Min(minimumIou, IntersectionOverUnion(pair.Expected.Box, pair.Actual.Box));
        maximumScoreAbsoluteError = Math.Max(
            maximumScoreAbsoluteError,
            Math.Abs(pair.Expected.Score - pair.Actual.Score));
    }

    actualSamples.Add(new SampleResult(
        names[index],
        inputSha256[index],
        expected.Length,
        actual.Length,
        actual,
        expected.Length - pairs.Count,
        actual.Length - pairs.Count));
}

const double minimumIouThreshold = 0.90;
const double maximumScoreAbsoluteErrorThreshold = 0.05;
const double minimumLabelAgreement = 1.0;
var labelAgreement = comparedDetections == 0 ? 0d : matchedLabels / (double)comparedDetections;
var countMatches = actualSamples.All(sample =>
    sample.ExpectedCount == sample.ActualCount && sample.UnmatchedExpected == 0);
if (double.IsPositiveInfinity(minimumIou)) minimumIou = 0;
var passed = comparedDetections > 0 &&
    countMatches &&
    labelAgreement >= minimumLabelAgreement &&
    minimumIou >= minimumIouThreshold &&
    maximumScoreAbsoluteError <= maximumScoreAbsoluteErrorThreshold;

var onnxVersion = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var skiaVersion = typeof(SkiaSharp.SKBitmap).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var environment = new
{
    dotnet = Environment.Version.ToString(),
    onnxRuntime = onnxVersion,
    skiaSharp = skiaVersion,
    os = Environment.OSVersion.ToString(),
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
};
var manifest = await ReadPortableManifestAsync(onnxModelPath, modelId);
var artifact = manifest.Files.Single(file => string.Equals(file.Path, onnxFile, StringComparison.Ordinal));
var report = new
{
    schemaVersion = 1,
    modelId,
    revision,
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    sampleCount = encodedImages.Length,
    detectionCount = comparedDetections,
    thresholds = new
    {
        minimumIou = minimumIouThreshold,
        maximumScoreAbsoluteError = maximumScoreAbsoluteErrorThreshold,
        minimumLabelAgreement,
        minimumDetectionCount = 1,
    },
    results = new { minimumIou, maximumScoreAbsoluteError, labelAgreement, comparedDetections, countMatches },
    samples = actualSamples,
    pythonEnvironment = gold.GetProperty("environment").Clone(),
    dotnetEnvironment = environment,
    artifact = new { artifact.Path, artifact.Size, artifact.Sha256, verified = true },
    onnxDownloadManifest = manifest,
};
var dotnetOutput = new
{
    schemaVersion = 1,
    modelId,
    revision,
    runtime = "ModelScope.Net / ONNX Runtime CPU / SkiaSharp",
    samples = actualSamples,
    environment,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(dotnetOutput, JsonOptions()) + Environment.NewLine);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
Console.WriteLine(
    $"object-detection-certification-{(passed ? "passed" : "failed")} samples={encodedImages.Length} " +
    $"detections={comparedDetections} min_iou={minimumIou:F6} max_score_abs={maximumScoreAbsoluteError:E6} " +
    $"label_agreement={labelAgreement:F6}");
return passed ? 0 : 2;

static Detection ReadDetection(JsonElement element)
{
    var index = element.TryGetProperty("index", out var indexElement)
        ? indexElement.GetInt32()
        : element.GetProperty("Index").GetInt32();
    var label = element.TryGetProperty("label", out var labelElement)
        ? labelElement.GetString()!
        : element.GetProperty("Label").GetString()!;
    var score = element.TryGetProperty("score", out var scoreElement)
        ? scoreElement.GetSingle()
        : element.GetProperty("Score").GetSingle();
    var boxElement = element.TryGetProperty("box", out var boxValue) ? boxValue : element.GetProperty("Box");
    var box = boxElement.EnumerateArray().Select(item => item.GetSingle()).ToArray();
    return new Detection(index, label, score, box);
}

static List<(Detection Expected, Detection Actual)> MatchDetections(Detection[] expected, Detection[] actual)
{
    var remaining = actual.ToList();
    var pairs = new List<(Detection, Detection)>();
    foreach (var candidate in expected.OrderByDescending(item => item.Score))
    {
        var bestIndex = -1;
        var bestIou = 0f;
        for (var index = 0; index < remaining.Count; index++)
        {
            if (remaining[index].Index != candidate.Index) continue;
            var iou = IntersectionOverUnion(candidate.Box, remaining[index].Box);
            if (iou > bestIou)
            {
                bestIou = iou;
                bestIndex = index;
            }
        }
        if (bestIndex < 0) continue;
        pairs.Add((candidate, remaining[bestIndex]));
        remaining.RemoveAt(bestIndex);
    }
    return pairs;
}

static float IntersectionOverUnion(float[] first, float[] second)
{
    var left = Math.Max(first[0], second[0]);
    var top = Math.Max(first[1], second[1]);
    var right = Math.Min(first[2], second[2]);
    var bottom = Math.Min(first[3], second[3]);
    var width = Math.Max(0, right - left);
    var height = Math.Max(0, bottom - top);
    var intersection = width * height;
    var union = (first[2] - first[0]) * (first[3] - first[1]) +
        (second[2] - second[0]) * (second[3] - second[1]) - intersection;
    return union <= 0 ? 0 : intersection / union;
}

static async Task<PortableManifest> ReadPortableManifestAsync(string modelPath, string modelId)
{
    using var document = JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(modelPath, ".modelscope-net-manifest.json")));
    var root = document.RootElement;
    return new PortableManifest(
        modelId,
        root.GetProperty("requestedRevision").GetString()!,
        root.GetProperty("resolvedRevision").GetString()!,
        root.GetProperty("files").EnumerateArray().Select(file => new PortableFile(
            file.GetProperty("path").GetString()!,
            file.GetProperty("size").GetInt64(),
            file.GetProperty("sha256").GetString()!)).ToArray());
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        result[values[index]] = values[index + 1];
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required argument '{name}' is missing.");

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

sealed record Detection(int Index, string Label, float Score, float[] Box);
sealed record SampleResult(
    string Name,
    string InputSha256,
    int ExpectedCount,
    int ActualCount,
    Detection[] Detections,
    int UnmatchedExpected,
    int UnmatchedActual);
sealed record PortableFile(string Path, long Size, string Sha256);
sealed record PortableManifest(string ModelId, string RequestedRevision, string ResolvedRevision, PortableFile[] Files);
