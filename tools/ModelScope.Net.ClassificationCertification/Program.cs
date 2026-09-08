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
var expectedLogits = ReadMatrix(gold.GetProperty("logits"));
var expectedProbabilities = ReadMatrix(gold.GetProperty("probabilities"));
var expectedLabels = gold.GetProperty("predictedLabels").EnumerateArray().Select(item => item.GetString()!).ToArray();
var revision = gold.GetProperty("revision").GetString()!;
var modelId = gold.GetProperty("modelId").GetString()!;
var onnxPath = Path.Combine(modelPath, "onnx", "model.onnx");

var capabilities = new ModelCapabilities(
    modelPath,
    modelId,
    revision,
    "text-classification",
    ["DistilBertForSequenceClassification"],
    [new ModelArtifact("onnx/model.onnx", ModelArtifactFormat.Onnx, new FileInfo(onnxPath).Length)],
    [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "certification", 100)],
    ContainsRemoteCode: false,
    Warnings: []);
var runtime = new OnnxTextClassificationRuntime(
    new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
    new OnnxTextClassificationOptions { MaxLength = 128 });

await using var session = await runtime.CreateSessionAsync(capabilities);
var payload = JsonSerializer.SerializeToElement(new { texts });
var response = await session.InvokeAsync(new ModelRequest("text-classification", payload));
var actualLogits = ReadMatrix(response.Output.GetProperty("logits"));
var predictions = response.Output.GetProperty("predictions").EnumerateArray().ToArray();
var actualLabels = predictions.Select(item => item.GetProperty("label").GetString()!).ToArray();
var actualProbabilities = actualLogits.Select(Softmax).ToArray();

var sampleCountMatches = actualLogits.Length == expectedLogits.Length && actualLogits.Length == texts.Length;
var shapeMatches = sampleCountMatches && expectedLogits.Zip(actualLogits).All(pair => pair.First.Length == pair.Second.Length);
var maximumLogitAbsoluteError = shapeMatches
    ? expectedLogits.Zip(actualLogits).SelectMany(pair => pair.First.Zip(pair.Second, (left, right) => Math.Abs(left - right))).Max()
    : double.PositiveInfinity;
var maximumProbabilityAbsoluteError = shapeMatches
    ? expectedProbabilities.Zip(actualProbabilities).SelectMany(pair => pair.First.Zip(pair.Second, (left, right) => Math.Abs(left - right))).Max()
    : double.PositiveInfinity;
var labelAgreement = expectedLabels.Length == actualLabels.Length
    ? expectedLabels.Zip(actualLabels).Count(pair => pair.First == pair.Second) / (double)expectedLabels.Length
    : 0d;

var samples = texts.Select((textValue, index) => new
{
    index,
    text = textValue,
    expectedLabel = expectedLabels[index],
    dotnetLabel = actualLabels[index],
    maximumLogitAbsoluteError = expectedLogits[index].Zip(actualLogits[index], (left, right) => Math.Abs(left - right)).Max(),
    maximumProbabilityAbsoluteError = expectedProbabilities[index].Zip(actualProbabilities[index], (left, right) => Math.Abs(left - right)).Max(),
}).ToArray();

const double maximumLogitAbsoluteErrorThreshold = 1e-4;
const double maximumProbabilityAbsoluteErrorThreshold = 1e-5;
const double minimumLabelAgreementThreshold = 1d;
var passed = shapeMatches &&
    maximumLogitAbsoluteError <= maximumLogitAbsoluteErrorThreshold &&
    maximumProbabilityAbsoluteError <= maximumProbabilityAbsoluteErrorThreshold &&
    labelAgreement >= minimumLabelAgreementThreshold;

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
var environment = new
{
    dotnet = Environment.Version.ToString(),
    onnxRuntime = onnxVersion,
    os = Environment.OSVersion.ToString(),
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
};
var dotnetOutput = new
{
    schemaVersion = 1,
    modelId,
    revision,
    runtime = "ModelScope.Net / ONNX Runtime CPU",
    scoreMode = "softmax",
    maxLength = 128,
    texts,
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
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    sampleCount = texts.Length,
    labelCount = actualLogits.FirstOrDefault()?.Length ?? 0,
    thresholds = new
    {
        maximumLogitAbsoluteError = maximumLogitAbsoluteErrorThreshold,
        maximumProbabilityAbsoluteError = maximumProbabilityAbsoluteErrorThreshold,
        minimumLabelAgreement = minimumLabelAgreementThreshold,
    },
    results = new { maximumLogitAbsoluteError, maximumProbabilityAbsoluteError, labelAgreement },
    samples,
    pythonEnvironment = gold.GetProperty("environment").Clone(),
    dotnetEnvironment = environment,
    downloadManifest = manifest,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(dotnetOutput, JsonOptions()) + Environment.NewLine);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
Console.WriteLine(
    $"classification-certification-{(passed ? "passed" : "failed")} samples={texts.Length} labels={report.labelCount} " +
    $"max_logit_abs={maximumLogitAbsoluteError:E6} max_probability_abs={maximumProbabilityAbsoluteError:E6} label_agreement={labelAgreement:F6}");
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

static double[] Softmax(IReadOnlyList<double> logits)
{
    var maximum = logits.Max();
    var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
    var sum = exponentials.Sum();
    return exponentials.Select(value => value / sum).ToArray();
}

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
