using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime.Gguf;

const string expectedRelease = "b10516";
const string expectedCommit = "b95502ba9";
const string expectedArchiveSha256 = "ee3324327d621026ae80c24031670e65fa62a0b23a3a027dbe2f65f240affd30";

var arguments = ParseArguments(args);
var executable = Path.GetFullPath(Require(arguments, "--executable"));
var archive = Path.GetFullPath(Require(arguments, "--archive"));
var modelPath = Path.GetFullPath(Require(arguments, "--model"));
var fileName = Require(arguments, "--file");
var reportPath = Path.GetFullPath(Require(arguments, "--report"));
var modelFile = ResolveChild(modelPath, fileName);

var archiveSha256 = await ComputeSha256Async(archive);
var executableSha256 = await ComputeSha256Async(executable);
var version = await ReadVersionAsync(executable);
var binaryVerified = archiveSha256 == expectedArchiveSha256 &&
    version.Contains($"build {expectedRelease[1..]}", StringComparison.Ordinal) &&
    version.Contains(expectedCommit, StringComparison.Ordinal);

using var manifestDocument = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(modelPath, ".modelscope-net-manifest.json")));
var manifest = manifestDocument.RootElement;
var modelId = $"{manifest.GetProperty("modelId").GetProperty("owner").GetString()}/" +
    manifest.GetProperty("modelId").GetProperty("name").GetString();
var revision = manifest.GetProperty("resolvedRevision").GetString()!;
var expectedArtifact = manifest.GetProperty("files").EnumerateArray()
    .Single(item => string.Equals(item.GetProperty("path").GetString(), fileName, StringComparison.Ordinal));
var expectedModelSha256 = expectedArtifact.GetProperty("sha256").GetString()!;
var expectedModelSize = expectedArtifact.GetProperty("size").GetInt64();
var modelSha256 = await ComputeSha256Async(modelFile);
var modelSize = GetContentLength(modelFile);
var modelVerified = modelSha256 == expectedModelSha256 && modelSize == expectedModelSize;

var port = GetFreePort();
var processOptions = new LlamaServerProcessOptions
{
    Executable = executable,
    Port = port,
    ContextSize = 512,
    Threads = Math.Min(4, Math.Max(1, Environment.ProcessorCount)),
    GpuLayers = 0,
    StartupTimeout = TimeSpan.FromMinutes(2),
    ShutdownTimeout = TimeSpan.FromSeconds(10),
    ModelAlias = "modelscope-net-certification",
};
await using var supervisor = new LocalLlamaServerSupervisor(processOptions);
using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var runtimeOptions = new GgufRuntimeOptions
{
    LlamaServerEndpoint = supervisor.Endpoint,
    ModelAlias = processOptions.ModelAlias,
    StartupTimeout = processOptions.StartupTimeout,
    RequestTimeout = TimeSpan.FromMinutes(2),
    DefaultMaxTokens = 32,
    MaxTokens = 64,
};
runtimeOptions.CertifiedModels.Add(modelId);
var runtime = new GgufRuntimeAdapter(httpClient, runtimeOptions, supervisor);
var capabilities = new ModelCapabilities(
    modelPath,
    modelId,
    revision,
    "text-generation",
    ["qwen2"],
    [new ModelArtifact(fileName, ModelArtifactFormat.Gguf, modelSize)],
    [new RuntimeCandidate("gguf", CapabilityStatus.Certified, "certification", 90)],
    ContainsRemoteCode: false,
    Warnings: []);

var loadTimer = Stopwatch.StartNew();
await using var session = await runtime.CreateSessionAsync(capabilities);
loadTimer.Stop();
var initialStatus = supervisor.GetStatus();
var initialProcessId = initialStatus.ProcessId ?? throw new InvalidOperationException("llama-server did not report a process ID.");

var nonStreaming = await InvokeExactAsync(session, "MODELSCOPE_DOTNET_OK");
var streamPayload = CreatePrompt("MODELSCOPE_STREAM_OK", stream: true);
var streamedContent = new List<string>();
var terminalObserved = false;
await foreach (var item in session.InvokeStreamingAsync(
    new ModelRequest("chat", streamPayload, Stream: true)))
{
    if (item.IsTerminal)
    {
        terminalObserved = true;
        continue;
    }
    var delta = item.Data.GetProperty("choices")[0].GetProperty("delta");
    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        streamedContent.Add(content.GetString()!);
}
var streamText = string.Concat(streamedContent).Trim();

using (var process = Process.GetProcessById(initialProcessId))
{
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
}
var recovery = await InvokeExactAsync(session, "MODELSCOPE_RECOVERY_OK");
var recoveredStatus = supervisor.GetStatus();
var processRecovered = recoveredStatus.IsRunning &&
    recoveredStatus.ProcessId is not null &&
    recoveredStatus.ProcessId != initialProcessId;
await supervisor.StopAsync();
var stoppedCleanly = !supervisor.GetStatus().IsRunning;

var passed = binaryVerified &&
    modelVerified &&
    nonStreaming.Content == "MODELSCOPE_DOTNET_OK" &&
    nonStreaming.FinishReason == "stop" &&
    streamText == "MODELSCOPE_STREAM_OK" &&
    terminalObserved &&
    recovery.Content == "MODELSCOPE_RECOVERY_OK" &&
    processRecovered &&
    stoppedCleanly;
var report = new
{
    schemaVersion = 1,
    modelId,
    revision,
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    llamaCpp = new
    {
        release = expectedRelease,
        commit = expectedCommit,
        version,
        archiveSha256,
        expectedArchiveSha256,
        executableSha256,
        binaryVerified,
        platform = "macos-arm64",
    },
    artifact = new
    {
        path = fileName,
        size = modelSize,
        sha256 = modelSha256,
        expectedSize = expectedModelSize,
        expectedSha256 = expectedModelSha256,
        verified = modelVerified,
    },
    execution = new
    {
        runtime = "ModelScope.Net.Runtime.Gguf / llama-server CPU",
        contextSize = processOptions.ContextSize,
        threads = processOptions.Threads,
        gpuLayers = processOptions.GpuLayers,
        loopbackOnly = true,
        loadMilliseconds = loadTimer.Elapsed.TotalMilliseconds,
        nonStreaming = new
        {
            expected = "MODELSCOPE_DOTNET_OK",
            actual = nonStreaming.Content,
            finishReason = nonStreaming.FinishReason,
            totalTokens = nonStreaming.TotalTokens,
        },
        streaming = new
        {
            expected = "MODELSCOPE_STREAM_OK",
            actual = streamText,
            terminalObserved,
        },
        crashRecovery = new
        {
            expected = "MODELSCOPE_RECOVERY_OK",
            actual = recovery.Content,
            processRecovered,
        },
        stoppedCleanly,
    },
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    },
};

Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(
    $"gguf-runtime-certification-{(passed ? "passed" : "failed")} model={modelId} " +
    $"release={expectedRelease} load_ms={loadTimer.Elapsed.TotalMilliseconds:F1} " +
    $"non_stream={nonStreaming.Content} stream={streamText} recovered={processRecovered} stopped={stoppedCleanly}");
return passed ? 0 : 2;

static async Task<(string Content, string? FinishReason, int TotalTokens)> InvokeExactAsync(
    IModelSession session,
    string expected)
{
    var response = await session.InvokeAsync(new ModelRequest("chat", CreatePrompt(expected, stream: false)));
    var choice = response.Output.GetProperty("choices")[0];
    var content = choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? string.Empty;
    var finishReason = choice.GetProperty("finish_reason").GetString();
    var totalTokens = response.Output.TryGetProperty("usage", out var usage) &&
        usage.TryGetProperty("total_tokens", out var total)
        ? total.GetInt32()
        : 0;
    return (content, finishReason, totalTokens);
}

static JsonElement CreatePrompt(string expected, bool stream) => JsonSerializer.SerializeToElement(new
{
    messages = new object[]
    {
        new { role = "system", content = "Reply with exactly the requested text and nothing else." },
        new { role = "user", content = $"Reply exactly: {expected}" },
    },
    temperature = 0,
    seed = 42,
    max_tokens = 32,
    stream,
});

static async Task<string> ComputeSha256Async(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static long GetContentLength(string path)
{
    using var stream = File.OpenRead(path);
    return stream.Length;
}

static async Task<string> ReadVersionAsync(string executable)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--version");
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not inspect llama-server version.");
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException("llama-server --version failed.");
    return string.Join(" ", (stdout + " " + stderr).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

static string ResolveChild(string root, string relativePath)
{
    var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var path = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
    if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new ArgumentException("The model file escapes the model directory.");
    return path;
}

static int GetFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
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
