using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Gguf;
using ModelScope.Net.Runtime.Onnx;

var arguments = ParseArguments(args);
var artifactRoot = Path.GetFullPath(Get(arguments, "--artifacts", "artifacts/certification"));
var llamaExecutable = Path.GetFullPath(Require(arguments, "--llama-executable"));
var outputPath = Path.GetFullPath(Get(arguments, "--output", "artifacts/benchmarks/p3-08-cpu-macos-arm64.json"));
var iterations = ParsePositiveInt(Get(arguments, "--iterations", "20"), "--iterations");
var warmups = ParsePositiveInt(Get(arguments, "--warmups", "2"), "--warmups");
var repeatRuns = ParsePositiveInt(Get(arguments, "--repeat-runs", "2"), "--repeat-runs");

var runs = new List<IReadOnlyList<ModelBenchmark>>();
for (var run = 0; run < repeatRuns; run++)
{
    var runResults = new List<ModelBenchmark>();
    runResults.Add(await BenchmarkEmbeddingAsync(
        Path.Combine(artifactRoot, "bge-small-en-v1.5"),
        "BAAI/bge-small-en-v1.5",
        "160f4d645d32abe3cabc5af6b6b39823eadf3c0e",
        OnnxEmbeddingPooling.Cls,
        iterations,
        warmups));
    runResults.Add(await BenchmarkEmbeddingAsync(
        Path.Combine(artifactRoot, "all-minilm-l6-v2"),
        "unsloth/all-MiniLM-L6-v2",
        "4bc149651c730bffa46308d38a3f2a2c5e9b6e08",
        OnnxEmbeddingPooling.Mean,
        iterations,
        warmups));
    runResults.Add(await BenchmarkClassificationAsync(
        Path.Combine(artifactRoot, "distilbert-sst2"),
        iterations,
        warmups));
    runResults.Add(await BenchmarkVisionAsync(
        Path.Combine(artifactRoot, "mobilenet-v2-onnx"),
        "tests/compatibility/mobilenet-v2-image-classification/inputs.json",
        iterations,
        warmups));
    runResults.Add(await BenchmarkGgufAsync(
        Path.Combine(artifactRoot, "qwen2.5-0.5b-instruct-gguf"),
        llamaExecutable,
        Math.Min(iterations, 5),
        1));
    runs.Add(runResults);
}
var results = AggregateRuns(runs);

var report = new
{
    schemaVersion = 1,
    status = results.All(result => result.Passed) ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    scope = "technical-preview-macos-arm64-cpu",
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        logicalProcessors = Environment.ProcessorCount,
        availableMemoryMiB = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d, 1),
        onnxRuntime = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        llamaCppRelease = "b10516",
        llamaCppCommit = "b95502ba9",
    },
    methodology = new
    {
        warmups,
        repeatRuns,
        onnxIterations = iterations,
        ggufIterations = Math.Min(iterations, 5),
        percentiles = "nearest-rank over end-to-end InvokeAsync latency",
        throughput = "completed input items or completion tokens divided by measured invocation wall time",
        memory = "maximum observed process working set; ONNX values include the benchmark host and GGUF values use llama-server",
        concurrency = "sequential requests; batch capacity is measured separately at batch 1 and 8",
        gpu = "not measured; GPU remains outside the current certified preview scope",
    },
    models = results,
    rawRuns = runs.Select((models, index) => new { run = index + 1, models }),
    summary = new
    {
        certifiedModels = results.Count,
        passedModels = results.Count(result => result.Passed),
        failedModels = results.Count(result => !result.Passed),
    },
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }) + Environment.NewLine);
Console.WriteLine($"performance-benchmark-{report.status} models={results.Count} output={Path.GetFileName(outputPath)}");
return report.status == "passed" ? 0 : 2;

static async Task<ModelBenchmark> BenchmarkEmbeddingAsync(
    string modelPath,
    string modelId,
    string revision,
    OnnxEmbeddingPooling pooling,
    int iterations,
    int warmups)
{
    var capabilities = Capabilities(
        modelPath,
        modelId,
        revision,
        "sentence-embedding",
        "BertModel",
        "onnx/model.onnx",
        ModelArtifactFormat.Onnx);
    var runtime = new OnnxEmbeddingRuntime(
        new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
        new OnnxEmbeddingOptions { MaxLength = 128, Pooling = pooling, Normalize = true });
    return await BenchmarkOnnxAsync(
        capabilities,
        runtime,
        "onnx-embedding",
        batch => new ModelRequest(
            "sentence-embedding",
            JsonSerializer.SerializeToElement(new
            {
                texts = Enumerable.Repeat("Semantic search with ModelScope.Net on the CPU.", batch).ToArray(),
            })),
        [1, 8],
        iterations,
        warmups,
        p95BatchOneThresholdMs: 250,
        p95BatchEightThresholdMs: 1000,
        minimumBatchOneThroughput: 4,
        minimumBatchEightThroughput: 16);
}

static async Task<ModelBenchmark> BenchmarkClassificationAsync(
    string modelPath,
    int iterations,
    int warmups)
{
    var capabilities = Capabilities(
        modelPath,
        "distilbert/distilbert-base-uncased-finetuned-sst-2-english",
        "ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f",
        "text-classification",
        "DistilBertForSequenceClassification",
        "onnx/model.onnx",
        ModelArtifactFormat.Onnx);
    var runtime = new OnnxTextClassificationRuntime(
        new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
        new OnnxTextClassificationOptions { MaxLength = 128 });
    return await BenchmarkOnnxAsync(
        capabilities,
        runtime,
        "onnx-text-classification",
        batch => new ModelRequest(
            "text-classification",
            JsonSerializer.SerializeToElement(new
            {
                texts = Enumerable.Repeat("The local ModelScope runtime works very well.", batch).ToArray(),
            })),
        [1, 8],
        iterations,
        warmups,
        p95BatchOneThresholdMs: 250,
        p95BatchEightThresholdMs: 1000,
        minimumBatchOneThroughput: 4,
        minimumBatchEightThroughput: 16);
}

static async Task<ModelBenchmark> BenchmarkVisionAsync(
    string modelPath,
    string inputsPath,
    int iterations,
    int warmups)
{
    using var inputs = JsonDocument.Parse(await File.ReadAllTextAsync(inputsPath));
    var image = inputs.RootElement[0].GetProperty("base64").GetString()!;
    var capabilities = Capabilities(
        modelPath,
        "onnx-community/mobilenet_v2_1.0_224-ONNX",
        "ba6621a361183d7ce00314802b63a46eee2aa48b",
        "image-classification",
        "MobileNetV2ForImageClassification",
        "onnx/model.onnx",
        ModelArtifactFormat.Onnx);
    var runtime = new OnnxImageClassificationRuntime(
        new OnnxRuntimeAdapter(new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" }),
        new OnnxImageClassificationOptions { TopK = 5 });
    return await BenchmarkOnnxAsync(
        capabilities,
        runtime,
        "onnx-image-classification",
        batch => new ModelRequest(
            "image-classification",
            JsonSerializer.SerializeToElement(new
            {
                images = Enumerable.Repeat(image, batch).ToArray(),
            })),
        [1, 8],
        iterations,
        warmups,
        p95BatchOneThresholdMs: 500,
        p95BatchEightThresholdMs: 2500,
        minimumBatchOneThroughput: 2,
        minimumBatchEightThroughput: 4);
}

static async Task<ModelBenchmark> BenchmarkOnnxAsync(
    ModelCapabilities capabilities,
    IModelRuntime runtime,
    string runtimeName,
    Func<int, ModelRequest> request,
    int[] batchSizes,
    int iterations,
    int warmups,
    double p95BatchOneThresholdMs,
    double p95BatchEightThresholdMs,
    double minimumBatchOneThroughput,
    double minimumBatchEightThroughput)
{
    Collect();
    var loadStart = Stopwatch.GetTimestamp();
    await using var session = await runtime.CreateSessionAsync(capabilities);
    var loadMilliseconds = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
    var peakWorkingSet = WorkingSet();
    var scenarios = new List<ScenarioBenchmark>();
    foreach (var batchSize in batchSizes)
    {
        for (var index = 0; index < warmups; index++)
            _ = await session.InvokeAsync(request(batchSize));
        var samples = new double[iterations];
        var totalStart = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = await session.InvokeAsync(request(batchSize));
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            peakWorkingSet = Math.Max(peakWorkingSet, WorkingSet());
        }
        var totalSeconds = Stopwatch.GetElapsedTime(totalStart).TotalSeconds;
        var p95Threshold = batchSize == 1 ? p95BatchOneThresholdMs : p95BatchEightThresholdMs;
        var throughputThreshold = batchSize == 1 ? minimumBatchOneThroughput : minimumBatchEightThroughput;
        var p95 = Percentile(samples, 0.95);
        var throughput = batchSize * iterations / totalSeconds;
        scenarios.Add(new ScenarioBenchmark(
            batchSize,
            iterations,
            Math.Round(samples.Average(), 3),
            Math.Round(Percentile(samples, 0.50), 3),
            Math.Round(p95, 3),
            Math.Round(throughput, 3),
            "items/s",
            new ScenarioThreshold(p95Threshold, throughputThreshold),
            p95 <= p95Threshold && throughput >= throughputThreshold));
    }
    const double loadThresholdMs = 5000;
    const double workingSetThresholdMiB = 2048;
    var workingSetMiB = peakWorkingSet / 1024d / 1024d;
    return new ModelBenchmark(
        capabilities.ModelId!,
        capabilities.Revision!,
        capabilities.Task!,
        runtimeName,
        Math.Round(loadMilliseconds, 3),
        Math.Round(workingSetMiB, 1),
        scenarios,
        new ModelThreshold(loadThresholdMs, workingSetThresholdMiB),
        loadMilliseconds <= loadThresholdMs && workingSetMiB <= workingSetThresholdMiB && scenarios.All(item => item.Passed));
}

static async Task<ModelBenchmark> BenchmarkGgufAsync(
    string modelPath,
    string executable,
    int iterations,
    int warmups)
{
    var port = FreePort();
    var processOptions = new LlamaServerProcessOptions
    {
        Executable = executable,
        Port = port,
        ContextSize = 512,
        Threads = Math.Min(4, Math.Max(1, Environment.ProcessorCount)),
        GpuLayers = 0,
        StartupTimeout = TimeSpan.FromMinutes(2),
    };
    await using var supervisor = new LocalLlamaServerSupervisor(processOptions);
    var runtimeOptions = new GgufRuntimeOptions
    {
        DefaultMaxTokens = 32,
        MaxTokens = 32,
        RequestTimeout = TimeSpan.FromMinutes(2),
    };
    runtimeOptions.CertifiedModels.Add("Qwen/Qwen2.5-0.5B-Instruct-GGUF");
    var runtime = new GgufRuntimeAdapter(null, runtimeOptions, supervisor);
    var capabilities = Capabilities(
        modelPath,
        "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
        "2e50b77b0eee3083842019e257b74854323d880a",
        "text-generation",
        "qwen2",
        "qwen2.5-0.5b-instruct-q2_k.gguf",
        ModelArtifactFormat.Gguf);
    var loadStart = Stopwatch.GetTimestamp();
    await using var session = await runtime.CreateSessionAsync(capabilities);
    var loadMilliseconds = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
    var request = new ModelRequest(
        "chat",
        JsonSerializer.SerializeToElement(new
        {
            messages = new[] { new { role = "user", content = "Write one short sentence about local inference." } },
            max_tokens = 32,
            temperature = 0,
            seed = 123,
        }));
    for (var index = 0; index < warmups; index++) _ = await session.InvokeAsync(request);
    var samples = new double[iterations];
    var completionTokens = 0;
    var peakWorkingSet = ChildWorkingSet(supervisor.GetStatus().ProcessId);
    var totalStart = Stopwatch.GetTimestamp();
    for (var index = 0; index < iterations; index++)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await session.InvokeAsync(request);
        samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        completionTokens += response.Output.GetProperty("usage").GetProperty("completion_tokens").GetInt32();
        peakWorkingSet = Math.Max(peakWorkingSet, ChildWorkingSet(supervisor.GetStatus().ProcessId));
    }
    var totalSeconds = Stopwatch.GetElapsedTime(totalStart).TotalSeconds;
    var p95 = Percentile(samples, 0.95);
    var tokenThroughput = completionTokens / totalSeconds;
    const double p95ThresholdMs = 10000;
    const double minimumTokensPerSecond = 5;
    var scenario = new ScenarioBenchmark(
        1,
        iterations,
        Math.Round(samples.Average(), 3),
        Math.Round(Percentile(samples, 0.50), 3),
        Math.Round(p95, 3),
        Math.Round(tokenThroughput, 3),
        "completion tokens/s",
        new ScenarioThreshold(p95ThresholdMs, minimumTokensPerSecond),
        p95 <= p95ThresholdMs && tokenThroughput >= minimumTokensPerSecond);
    const double loadThresholdMs = 10000;
    const double workingSetThresholdMiB = 2048;
    var workingSetMiB = peakWorkingSet / 1024d / 1024d;
    await supervisor.StopAsync();
    var stopped = !supervisor.GetStatus().IsRunning;
    return new ModelBenchmark(
        capabilities.ModelId!,
        capabilities.Revision!,
        capabilities.Task!,
        "llama.cpp-b10516-cpu",
        Math.Round(loadMilliseconds, 3),
        Math.Round(workingSetMiB, 1),
        [scenario],
        new ModelThreshold(loadThresholdMs, workingSetThresholdMiB),
        loadMilliseconds <= loadThresholdMs && workingSetMiB <= workingSetThresholdMiB && scenario.Passed && stopped);
}

static ModelCapabilities Capabilities(
    string modelPath,
    string modelId,
    string revision,
    string task,
    string architecture,
    string artifact,
    ModelArtifactFormat format)
{
    var fullModelPath = Path.GetFullPath(modelPath);
    var fullArtifact = Path.Combine(fullModelPath, artifact);
    if (!File.Exists(fullArtifact)) throw new FileNotFoundException("Benchmark artifact was not found.", fullArtifact);
    return new ModelCapabilities(
        fullModelPath,
        modelId,
        revision,
        task,
        [architecture],
        [new ModelArtifact(artifact, format, new FileInfo(fullArtifact).Length)],
        [new RuntimeCandidate(format == ModelArtifactFormat.Gguf ? "gguf" : "onnx", CapabilityStatus.Certified, "benchmark", 100)],
        ContainsRemoteCode: false,
        Warnings: []);
}

static double Percentile(IEnumerable<double> values, double percentile)
{
    var ordered = values.OrderBy(value => value).ToArray();
    var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
    return ordered[index];
}

static IReadOnlyList<ModelBenchmark> AggregateRuns(IReadOnlyList<IReadOnlyList<ModelBenchmark>> runs)
{
    if (runs.Count == 0) return [];
    return Enumerable.Range(0, runs[0].Count).Select(modelIndex =>
    {
        var models = runs.Select(run => run[modelIndex]).ToArray();
        var scenarios = Enumerable.Range(0, models[0].Scenarios.Count).Select(scenarioIndex =>
        {
            var values = models.Select(model => model.Scenarios[scenarioIndex]).ToArray();
            return new ScenarioBenchmark(
                values[0].BatchSize,
                values[0].IterationsPerRun,
                Math.Round(values.Average(value => value.MeanMilliseconds), 3),
                Math.Round(values.Max(value => value.P50Milliseconds), 3),
                Math.Round(values.Max(value => value.P95Milliseconds), 3),
                Math.Round(values.Min(value => value.Throughput), 3),
                values[0].ThroughputUnit,
                values[0].Threshold,
                values.All(value => value.Passed),
                new ScenarioRange(
                    values.Min(value => value.P95Milliseconds),
                    values.Max(value => value.P95Milliseconds),
                    values.Min(value => value.Throughput),
                    values.Max(value => value.Throughput)));
        }).ToArray();
        return new ModelBenchmark(
            models[0].ModelId,
            models[0].Revision,
            models[0].Task,
            models[0].Runtime,
            models.Max(model => model.LoadMilliseconds),
            models.Max(model => model.PeakWorkingSetMiB),
            scenarios,
            models[0].Threshold,
            models.All(model => model.Passed),
            new ModelRange(
                models.Min(model => model.LoadMilliseconds),
                models.Max(model => model.LoadMilliseconds),
                models.Min(model => model.PeakWorkingSetMiB),
                models.Max(model => model.PeakWorkingSetMiB)));
    }).ToArray();
}

static long WorkingSet()
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    return process.WorkingSet64;
}

static long ChildWorkingSet(int? processId)
{
    if (processId is null) return 0;
    using var process = Process.GetProcessById(processId.Value);
    process.Refresh();
    return process.WorkingSet64;
}

static void Collect()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static int ParsePositiveInt(string value, string name) =>
    int.TryParse(value, out var parsed) && parsed > 0
        ? parsed
        : throw new ArgumentException($"{name} must be a positive integer.");

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

static string Get(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

internal sealed record ScenarioThreshold(double MaximumP95Milliseconds, double MinimumThroughput);

internal sealed record ScenarioBenchmark(
    int BatchSize,
    int IterationsPerRun,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double Throughput,
    string ThroughputUnit,
    ScenarioThreshold Threshold,
    bool Passed,
    ScenarioRange? ObservedRange = null);

internal sealed record ScenarioRange(
    double MinimumP95Milliseconds,
    double MaximumP95Milliseconds,
    double MinimumThroughput,
    double MaximumThroughput);

internal sealed record ModelThreshold(double MaximumLoadMilliseconds, double MaximumWorkingSetMiB);

internal sealed record ModelBenchmark(
    string ModelId,
    string Revision,
    string Task,
    string Runtime,
    double LoadMilliseconds,
    double PeakWorkingSetMiB,
    IReadOnlyList<ScenarioBenchmark> Scenarios,
    ModelThreshold Threshold,
    bool Passed,
    ModelRange? ObservedRange = null);

internal sealed record ModelRange(
    double MinimumLoadMilliseconds,
    double MaximumLoadMilliseconds,
    double MinimumPeakWorkingSetMiB,
    double MaximumPeakWorkingSetMiB);
