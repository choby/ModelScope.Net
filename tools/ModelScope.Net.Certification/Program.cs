using System.Reflection;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Onnx;

var arguments = ParseArguments(args);
var modelPath = Path.GetFullPath(Require(arguments, "--model"));
var goldPath = Path.GetFullPath(Require(arguments, "--gold"));
var outputPath = Path.GetFullPath(Require(arguments, "--output"));
var reportPath = Path.GetFullPath(Require(arguments, "--report"));

using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath));
var gold = goldDocument.RootElement;
var texts = gold.GetProperty("texts").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
var expected = ReadMatrix(gold.GetProperty("embeddings"));
var revision = gold.GetProperty("revision").GetString()!;
var modelId = gold.GetProperty("modelId").GetString()!;
var poolingName = gold.GetProperty("pooling").GetString() ?? throw new InvalidDataException("Gold pooling is missing.");
var pooling = poolingName.ToLowerInvariant() switch
{
    "cls" => OnnxEmbeddingPooling.Cls,
    "mean" => OnnxEmbeddingPooling.Mean,
    _ => throw new InvalidDataException($"Unsupported gold pooling '{poolingName}'."),
};
var onnxPath = Path.Combine(modelPath, "onnx", "model.onnx");

var capabilities = new ModelCapabilities(
    modelPath,
    modelId,
    revision,
    "sentence-embedding",
    ["BertModel"],
    [new ModelArtifact("onnx/model.onnx", ModelArtifactFormat.Onnx, new FileInfo(onnxPath).Length)],
    [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "certification", 100)],
    ContainsRemoteCode: false,
    Warnings: []);
var runtime = new OnnxEmbeddingRuntime(
    new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
    new OnnxEmbeddingOptions
    {
        MaxLength = 128,
        Pooling = pooling,
        Normalize = true,
    });

await using var session = await runtime.CreateSessionAsync(capabilities);
var payload = JsonSerializer.SerializeToElement(new { texts });
var response = await session.InvokeAsync(new ModelRequest("sentence-embedding", payload));
var actual = ReadMatrix(response.Output.GetProperty("embeddings"));

var samples = new List<object>();
var maximumAbsoluteError = 0d;
var minimumCosineSimilarity = 1d;
for (var index = 0; index < expected.Length; index++)
{
    var maxAbsoluteError = expected[index].Zip(actual[index], (left, right) => Math.Abs(left - right)).Max();
    var meanAbsoluteError = expected[index].Zip(actual[index], (left, right) => Math.Abs(left - right)).Average();
    var cosineSimilarity = Cosine(expected[index], actual[index]);
    maximumAbsoluteError = Math.Max(maximumAbsoluteError, maxAbsoluteError);
    minimumCosineSimilarity = Math.Min(minimumCosineSimilarity, cosineSimilarity);
    samples.Add(new
    {
        index,
        text = texts[index],
        maxAbsoluteError,
        meanAbsoluteError,
        cosineSimilarity,
        pythonNorm = Norm(expected[index]),
        dotnetNorm = Norm(actual[index]),
    });
}

const double maximumAbsoluteErrorThreshold = 1e-4;
const double minimumCosineSimilarityThreshold = 0.99999;
var passed = actual.Length == expected.Length &&
    expected.Length > 0 &&
    actual.All(row => row.Length == expected[0].Length) &&
    maximumAbsoluteError <= maximumAbsoluteErrorThreshold &&
    minimumCosineSimilarity >= minimumCosineSimilarityThreshold;
using var manifestDocument = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(modelPath, ".modelscope-net-manifest.json")));
var manifestRoot = manifestDocument.RootElement;
var manifest = new
{
    modelId,
    requestedRevision = manifestRoot.GetProperty("requestedRevision").GetString(),
    resolvedRevision = manifestRoot.GetProperty("resolvedRevision").GetString(),
    files = manifestRoot.GetProperty("files").EnumerateArray().Select(file => new
    {
        path = file.GetProperty("path").GetString(),
        size = file.GetProperty("size").GetInt64(),
        sha256 = file.GetProperty("sha256").GetString(),
    }).ToArray(),
};
var onnxVersion = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

var dotnetOutput = new
{
    schemaVersion = 1,
    modelId,
    revision,
    runtime = "ModelScope.Net / ONNX Runtime CPU",
    pooling = poolingName,
    normalize = true,
    maxLength = 128,
    texts,
    embeddings = actual,
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        onnxRuntime = onnxVersion,
        os = Environment.OSVersion.ToString(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    },
};
var report = new
{
    schemaVersion = 1,
    modelId,
    revision,
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    sampleCount = texts.Length,
    dimensions = actual.FirstOrDefault()?.Length ?? 0,
    thresholds = new { maximumAbsoluteError = maximumAbsoluteErrorThreshold, minimumCosineSimilarity = minimumCosineSimilarityThreshold },
    results = new { maximumAbsoluteError, minimumCosineSimilarity },
    samples,
    pythonEnvironment = gold.GetProperty("environment").Clone(),
    dotnetEnvironment = dotnetOutput.environment,
    downloadManifest = manifest,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(dotnetOutput, JsonOptions()) + Environment.NewLine);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
Console.WriteLine($"certification-{(passed ? "passed" : "failed")} samples={texts.Length} dimensions={report.dimensions} max_abs={maximumAbsoluteError:E6} min_cos={minimumCosineSimilarity:F9}");
return passed ? 0 : 2;

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

static double Norm(IReadOnlyList<double> vector) => Math.Sqrt(vector.Sum(value => value * value));

static double Cosine(IReadOnlyList<double> left, IReadOnlyList<double> right)
{
    var dot = 0d;
    for (var index = 0; index < left.Count; index++) dot += left[index] * right[index];
    return dot / (Norm(left) * Norm(right));
}

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
