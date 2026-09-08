using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Onnx;

var arguments = ParseArguments(args);
var onnxModelPath = Path.GetFullPath(Require(arguments, "--onnx-model"));
var sourceModelPath = Path.GetFullPath(Require(arguments, "--source-model"));
var inputsPath = Path.GetFullPath(Require(arguments, "--inputs"));
var goldPath = Path.GetFullPath(Require(arguments, "--gold"));
var outputPath = Path.GetFullPath(Require(arguments, "--output"));
var reportPath = Path.GetFullPath(Require(arguments, "--report"));

using var inputsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(inputsPath));
var inputItems = inputsDocument.RootElement.EnumerateArray().ToArray();
var imageNames = inputItems.Select(item => item.GetProperty("name").GetString()!).ToArray();
var encodedImages = inputItems.Select(item => item.GetProperty("base64").GetString()!).ToArray();
var inputSha256 = encodedImages.Select(value =>
    Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(value)))).ToArray();

using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath));
var gold = goldDocument.RootElement;
var expectedLogits = ReadMatrix(gold.GetProperty("logits"));
var expectedProbabilities = ReadMatrix(gold.GetProperty("probabilities"));
var expectedIndices = gold.GetProperty("predictedIndices").EnumerateArray().Select(item => item.GetInt32()).ToArray();
var modelId = gold.GetProperty("onnxModelId").GetString()!;
var revision = gold.GetProperty("onnxRevision").GetString()!;
var onnxPath = Path.Combine(onnxModelPath, "onnx", "model.onnx");

var capabilities = new ModelCapabilities(
    onnxModelPath,
    modelId,
    revision,
    "image-classification",
    ["MobileNetV2ForImageClassification"],
    [new ModelArtifact("onnx/model.onnx", ModelArtifactFormat.Onnx, new FileInfo(onnxPath).Length)],
    [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "certification", 100)],
    ContainsRemoteCode: false,
    Warnings: []);
var runtime = new OnnxImageClassificationRuntime(
    new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
    new OnnxImageClassificationOptions { TopK = 5 });

await using var session = await runtime.CreateSessionAsync(capabilities);
var response = await session.InvokeAsync(new ModelRequest(
    "image-classification",
    JsonSerializer.SerializeToElement(new { images = encodedImages })));
var actualLogits = ReadMatrix(response.Output.GetProperty("logits"));
var actualProbabilities = actualLogits.Select(Softmax).ToArray();
var actualPredictions = response.Output.GetProperty("predictions").EnumerateArray().ToArray();
var actualIndices = actualPredictions.Select(item => item.GetProperty("index").GetInt32()).ToArray();
var actualLabels = actualPredictions.Select(item => item.GetProperty("label").GetString()!).ToArray();

var shapeMatches = actualLogits.Length == expectedLogits.Length &&
    actualLogits.Length == encodedImages.Length &&
    expectedLogits.Zip(actualLogits).All(pair => pair.First.Length == pair.Second.Length);
var maximumLogitAbsoluteError = shapeMatches
    ? expectedLogits.Zip(actualLogits)
        .SelectMany(pair => pair.First.Zip(pair.Second, (left, right) => Math.Abs(left - right))).Max()
    : double.PositiveInfinity;
var maximumProbabilityAbsoluteError = shapeMatches
    ? expectedProbabilities.Zip(actualProbabilities)
        .SelectMany(pair => pair.First.Zip(pair.Second, (left, right) => Math.Abs(left - right))).Max()
    : double.PositiveInfinity;
var top1Agreement = expectedIndices.Length == actualIndices.Length
    ? expectedIndices.Zip(actualIndices).Count(pair => pair.First == pair.Second) / (double)expectedIndices.Length
    : 0d;
var top5Overlaps = expectedProbabilities.Zip(actualProbabilities).Select(pair =>
{
    var expectedTop = TopIndices(pair.First, 5).ToHashSet();
    return TopIndices(pair.Second, 5).Count(expectedTop.Contains) / 5d;
}).ToArray();
var minimumTop5Overlap = top5Overlaps.Min();

var samples = imageNames.Select((name, index) => new
{
    index,
    name,
    inputSha256 = inputSha256[index],
    expectedIndex = expectedIndices[index],
    dotnetIndex = actualIndices[index],
    dotnetLabel = actualLabels[index],
    maximumLogitAbsoluteError = expectedLogits[index]
        .Zip(actualLogits[index], (left, right) => Math.Abs(left - right)).Max(),
    maximumProbabilityAbsoluteError = expectedProbabilities[index]
        .Zip(actualProbabilities[index], (left, right) => Math.Abs(left - right)).Max(),
    top5Overlap = top5Overlaps[index],
}).ToArray();

const double maximumLogitAbsoluteErrorThreshold = 0.25;
const double maximumProbabilityAbsoluteErrorThreshold = 0.02;
const double minimumTop1AgreementThreshold = 1d;
const double minimumTop5OverlapThreshold = 0.8;
var passed = shapeMatches &&
    maximumLogitAbsoluteError <= maximumLogitAbsoluteErrorThreshold &&
    maximumProbabilityAbsoluteError <= maximumProbabilityAbsoluteErrorThreshold &&
    top1Agreement >= minimumTop1AgreementThreshold &&
    minimumTop5Overlap >= minimumTop5OverlapThreshold;

var onnxManifest = await ReadPortableManifestAsync(onnxModelPath, modelId);
var sourceManifest = await ReadPortableManifestAsync(
    sourceModelPath,
    gold.GetProperty("sourceModelId").GetString()!);
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
var dotnetOutput = new
{
    schemaVersion = 1,
    modelId,
    revision,
    runtime = "ModelScope.Net / ONNX Runtime CPU / SkiaSharp",
    imageNames,
    inputSha256,
    predictedIndices = actualIndices,
    predictedLabels = actualLabels,
    logits = actualLogits,
    probabilities = actualProbabilities,
    environment,
};
var report = new
{
    schemaVersion = 1,
    modelId,
    revision,
    sourceModelId = gold.GetProperty("sourceModelId").GetString(),
    sourceRevision = gold.GetProperty("sourceRevision").GetString(),
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    sampleCount = encodedImages.Length,
    labelCount = actualLogits.FirstOrDefault()?.Length ?? 0,
    thresholds = new
    {
        maximumLogitAbsoluteError = maximumLogitAbsoluteErrorThreshold,
        maximumProbabilityAbsoluteError = maximumProbabilityAbsoluteErrorThreshold,
        minimumTop1Agreement = minimumTop1AgreementThreshold,
        minimumTop5Overlap = minimumTop5OverlapThreshold,
    },
    results = new { maximumLogitAbsoluteError, maximumProbabilityAbsoluteError, top1Agreement, minimumTop5Overlap },
    samples,
    preprocessing = gold.GetProperty("preprocessing").Clone(),
    pythonEnvironment = gold.GetProperty("environment").Clone(),
    dotnetEnvironment = environment,
    onnxDownloadManifest = onnxManifest,
    sourceDownloadManifest = sourceManifest,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(dotnetOutput, JsonOptions()) + Environment.NewLine);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
Console.WriteLine(
    $"vision-certification-{(passed ? "passed" : "failed")} samples={encodedImages.Length} labels={report.labelCount} " +
    $"max_logit_abs={maximumLogitAbsoluteError:E6} max_probability_abs={maximumProbabilityAbsoluteError:E6} " +
    $"top1={top1Agreement:F6} min_top5={minimumTop5Overlap:F6}");
return passed ? 0 : 2;

static async Task<object> ReadPortableManifestAsync(string modelPath, string modelId)
{
    using var document = JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(modelPath, ".modelscope-net-manifest.json")));
    var root = document.RootElement;
    return new
    {
        modelId,
        requestedRevision = root.GetProperty("requestedRevision").GetString(),
        resolvedRevision = root.GetProperty("resolvedRevision").GetString(),
        files = root.GetProperty("files").EnumerateArray().Select(file => new
        {
            path = file.GetProperty("path").GetString(),
            size = file.GetProperty("size").GetInt64(),
            sha256 = file.GetProperty("sha256").GetString(),
        }).ToArray(),
    };
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

static double[][] ReadMatrix(JsonElement element) => element.EnumerateArray()
    .Select(row => row.EnumerateArray().Select(item => item.GetDouble()).ToArray())
    .ToArray();

static double[] Softmax(IReadOnlyList<double> logits)
{
    var maximum = logits.Max();
    var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
    var sum = exponentials.Sum();
    return exponentials.Select(value => value / sum).ToArray();
}

static int[] TopIndices(IReadOnlyList<double> values, int count) => values
    .Select((value, index) => (value, index))
    .OrderByDescending(item => item.value)
    .ThenBy(item => item.index)
    .Take(count)
    .Select(item => item.index)
    .ToArray();

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
