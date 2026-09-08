using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Gguf;

var arguments = ParseArguments(args);
var executable = Path.GetFullPath(Require(arguments, "--executable"));
var modelPath = Path.GetFullPath(Require(arguments, "--model"));
var fileName = arguments.GetValueOrDefault("--file", "bge-base-en-v1.5.Q4_K_M.gguf");
var goldPath = Path.GetFullPath(Require(arguments, "--gold"));
var outputPath = Path.GetFullPath(Require(arguments, "--output"));
var reportPath = Path.GetFullPath(Require(arguments, "--report"));

using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath));
var gold = goldDocument.RootElement;
var modelId = gold.GetProperty("modelId").GetString()!;
var revision = gold.GetProperty("revision").GetString()!;
var texts = gold.GetProperty("texts").EnumerateArray().Select(item => item.GetString()!).ToArray();
var expected = gold.GetProperty("embeddings").EnumerateArray()
    .Select(row => row.EnumerateArray().Select(item => item.GetDouble()).ToArray())
    .ToArray();
var modelFile = Path.Combine(modelPath, fileName);
await using var modelStream = File.OpenRead(modelFile);
var modelSize = modelStream.Length;
var modelSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(modelStream));

var port = GetFreePort();
var processOptions = new LlamaServerProcessOptions
{
    Executable = executable,
    Port = port,
    ContextSize = 512,
    Threads = Math.Min(4, Math.Max(1, Environment.ProcessorCount)),
    GpuLayers = 0,
    StartupTimeout = TimeSpan.FromMinutes(2),
    ShutdownTimeout = TimeSpan.FromSeconds(15),
    ModelAlias = "modelscope-net-embedding-cert",
    EmbeddingsOnly = true,
    Pooling = "cls",
    EmbeddingNormalize = 2,
};
await using var supervisor = new LocalLlamaServerSupervisor(processOptions);
using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var runtimeOptions = new GgufRuntimeOptions
{
    LlamaServerEndpoint = supervisor.Endpoint,
    ModelAlias = processOptions.ModelAlias,
    StartupTimeout = processOptions.StartupTimeout,
    RequestTimeout = TimeSpan.FromMinutes(2),
};
runtimeOptions.CertifiedModels.Add(modelId);
var runtime = new GgufRuntimeAdapter(httpClient, runtimeOptions, supervisor);
var capabilities = new ModelCapabilities(
    modelPath,
    modelId,
    revision,
    "feature-extraction",
    ["bert"],
    [new ModelArtifact(fileName, ModelArtifactFormat.Gguf, modelSize)],
    [new RuntimeCandidate("gguf", CapabilityStatus.Compatible, "certification", 90)],
    ContainsRemoteCode: false,
    Warnings: []);

await using var session = await runtime.CreateSessionAsync(capabilities);
var actual = new double[texts.Length][];
for (var index = 0; index < texts.Length; index++)
{
    var response = await session.InvokeAsync(new ModelRequest(
        "feature-extraction",
        JsonSerializer.SerializeToElement(new { input = texts[index] })));
    actual[index] = ReadEmbedding(response.Output);
}

var dimensions = actual[0].Length;
var cosine = expected.Zip(actual, Cosine).ToArray();
var maximumAbsoluteError = expected.Zip(actual, (left, right) =>
    left.Length == right.Length
        ? left.Zip(right, (a, b) => Math.Abs(a - b)).Max()
        : double.PositiveInfinity).Max();
var minimumCosineSimilarity = cosine.Min();
const double maximumAbsoluteErrorThreshold = 0.05;
const double minimumCosineSimilarityThreshold = 0.95;
var passed = actual.Length == expected.Length &&
    actual.All(row => row.Length == expected[0].Length) &&
    minimumCosineSimilarity >= minimumCosineSimilarityThreshold &&
    maximumAbsoluteError <= maximumAbsoluteErrorThreshold;

var environment = new
{
    dotnet = Environment.Version.ToString(),
    llamaServer = executable,
    os = Environment.OSVersion.ToString(),
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    llamaAssembly = typeof(GgufRuntimeAdapter).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
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
    sampleCount = texts.Length,
    dimensions,
    pooling = "cls",
    normalize = true,
    thresholds = new
    {
        maximumAbsoluteError = maximumAbsoluteErrorThreshold,
        minimumCosineSimilarity = minimumCosineSimilarityThreshold,
    },
    results = new { maximumAbsoluteError, minimumCosineSimilarity, cosine },
    artifact = new { path = fileName, size = modelSize, sha256 = modelSha256, verified = true },
    pythonEnvironment = gold.GetProperty("environment").Clone(),
    dotnetEnvironment = environment,
};
var output = new
{
    schemaVersion = 1,
    modelId,
    revision,
    runtime = "ModelScope.Net / llama-server embeddings",
    texts,
    embeddings = actual,
    environment,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions()) + Environment.NewLine);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
Console.WriteLine(
    $"gguf-embedding-certification-{(passed ? "passed" : "failed")} samples={texts.Length} dim={dimensions} " +
    $"min_cosine={minimumCosineSimilarity:F6} max_abs={maximumAbsoluteError:E6}");
return passed ? 0 : 2;

static double[] ReadEmbedding(JsonElement output)
{
    if (output.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
    {
        var first = data[0];
        if (first.TryGetProperty("embedding", out var embedding))
            return embedding.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    }
    if (output.TryGetProperty("embedding", out var direct))
        return direct.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    throw new InvalidOperationException("llama-server embedding response did not contain a vector.");
}

static double Cosine(double[] left, double[] right)
{
    if (left.Length != right.Length || left.Length == 0) return double.NegativeInfinity;
    double dot = 0, leftNorm = 0, rightNorm = 0;
    for (var index = 0; index < left.Length; index++)
    {
        dot += left[index] * right[index];
        leftNorm += left[index] * left[index];
        rightNorm += right[index] * right[index];
    }
    var denominator = Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm);
    return denominator == 0 ? double.NegativeInfinity : dot / denominator;
}

static int GetFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
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

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
