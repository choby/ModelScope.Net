using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Python;

if (args.Length != 2) throw new ArgumentException("Usage: WorkerCertification NET_ROOT ORIGINAL_SOURCE_ROOT");
var root = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var outputDirectory = Path.Combine(root, "artifacts", "worker-certification", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(outputDirectory);
var report = new Dictionary<string, object?>
{
    ["schemaVersion"] = 1, ["status"] = "failed", ["generatedAt"] = DateTimeOffset.UtcNow,
    ["scope"] = "macOS local Python Worker gRPC classification; not container or production certification",
    ["probabilityTolerance"] = 0.0001, ["logitsCompared"] = false,
};
LocalPythonWorkerSupervisor? supervisor = null;
int? workerPid = null;
var ownedPids = new HashSet<int>();
var passed = false;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = timeout.Token;
try
{
    using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/compatibility/certification-plan.json"), token));
    var entries = plan.RootElement.GetProperty("models");
    var entry = entries.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "distilbert-sst2");
    var snapshot = entry.GetProperty("snapshots")[0];
    var modelPath = Path.Combine(root, snapshot.GetProperty("directory").GetString()!);
    var hashes = new Dictionary<string, string>();
    foreach (var file in snapshot.GetProperty("files").EnumerateArray())
    {
        var relative = file.GetProperty("path").GetString()!;
        var path = Path.Combine(modelPath, relative);
        using (var contents = File.OpenRead(path))
            if (contents.Length != file.GetProperty("size").GetInt64()) throw new InvalidDataException("Artifact size mismatch");
        var hash = await HashAsync(path, token);
        if (hash != file.GetProperty("sha256").GetString()) throw new InvalidDataException("Artifact hash mismatch");
        hashes[relative] = hash;
    }
    var goldAsset = entry.GetProperty("assets").GetProperty("gold");
    var goldPath = Path.Combine(root, goldAsset.GetProperty("path").GetString()!);
    var goldHash = await HashAsync(goldPath, token);
    if (goldHash != goldAsset.GetProperty("sha256").GetString()) throw new InvalidDataException("Gold hash mismatch");
    using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, token));
    var modelId = entry.GetProperty("modelId").GetString()!;
    var revision = entry.GetProperty("revision").GetString()!;
    if (gold.RootElement.GetProperty("modelId").GetString() != modelId || gold.RootElement.GetProperty("revision").GetString() != revision)
        throw new InvalidDataException("Gold identity mismatch");
    report["modelId"] = modelId; report["revision"] = revision; report["artifactHashes"] = hashes; report["goldSha256"] = goldHash;
    var script = Path.Combine(root, "worker/python/server.py");
    report["workerSha256"] = await HashAsync(script, token);
    var python = Path.Combine(root, ".certification-venv/bin/python");
    var workerEnvironment = new Dictionary<string, string>
    {
        ["PYTHONPATH"] = Path.Combine(root, ".worker-model-deps") + Path.PathSeparator + source,
        ["MODELSCOPE_CACHE"] = Path.Combine(root, ".worker-model-deps/cache"),
        ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
    };
    var environmentScript = Path.Combine(root, "worker/python/validation_environment.py");
    report["environmentProbeSha256"] = await HashAsync(environmentScript, token);
    var probeInfo = new ProcessStartInfo(python)
    {
        WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    probeInfo.ArgumentList.Add(environmentScript); probeInfo.ArgumentList.Add(source);
    probeInfo.ArgumentList.Add(Path.Combine(root, ".worker-model-deps"));
    WorkerProcessSecurityPolicy.ApplyEnvironment(probeInfo, workerEnvironment, new());
    probeInfo.Environment["PYTHONNOUSERSITE"] = "1";
    probeInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
    probeInfo.Environment["TOKENIZERS_PARALLELISM"] = "false";
    using (var probe = Process.Start(probeInfo) ?? throw new InvalidOperationException("Environment probe start failed"))
    {
        var stdout = probe.StandardOutput.ReadToEndAsync(token);
        var stderr = probe.StandardError.ReadToEndAsync(token);
        try
        {
            await probe.WaitForExitAsync(token);
            var environmentJson = await stdout;
            await stderr; // Never copy unfiltered dependency diagnostics to the report.
            var environmentPath = Path.Combine(outputDirectory, "environment.json");
            await File.WriteAllTextAsync(environmentPath, environmentJson, token);
            report["environmentSha256"] = await HashAsync(environmentPath, token);
            report["environmentReport"] = "environment.json";
            using var environment = JsonDocument.Parse(environmentJson);
            if (probe.ExitCode != 0 || environment.RootElement.GetProperty("status").GetString() != "passed")
                throw new InvalidDataException("Environment validation failed");
        }
        finally
        {
            if (!probe.HasExited) { probe.Kill(entireProcessTree: true); await probe.WaitForExitAsync(); }
        }
    }
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    supervisor = new LocalPythonWorkerSupervisor(new()
    {
        PythonExecutable = python,
        WorkerScript = script, WorkingDirectory = root,
        Arguments = { "--port", port.ToString(), "--backend", "modelscope", "--max-sessions", "1", "--workers", "1" },
        AllowedModelRoots = { modelPath }, Security = { CaptureDiagnostics = true },
        Environment =
        {
            ["PYTHONPATH"] = workerEnvironment["PYTHONPATH"],
            ["MODELSCOPE_CACHE"] = workerEnvironment["MODELSCOPE_CACHE"],
            ["HF_HUB_OFFLINE"] = workerEnvironment["HF_HUB_OFFLINE"],
            ["TRANSFORMERS_OFFLINE"] = workerEnvironment["TRANSFORMERS_OFFLINE"],
        },
    });
    using var http = new HttpClient();
    using var runtime = new GrpcPythonWorkerRuntime(http, new()
    {
        Endpoint = new Uri($"http://127.0.0.1:{port}"), Transport = PythonWorkerTransport.Grpc,
        RequestTimeout = TimeSpan.FromSeconds(90), RecoveryAttempts = 20, RecoveryDelay = TimeSpan.FromMilliseconds(250),
    }, supervisor);
    var capabilities = new ModelCapabilities(modelPath, modelId, revision, "text-classification", [],
        [new("model.safetensors", ModelArtifactFormat.SafeTensors, snapshot.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "model.safetensors").GetProperty("size").GetInt64())],
        [new("python", CapabilityStatus.Detected, "Fixed local classification validation", 50)], false, []);
    JsonElement response;
    var request = new ModelRequest("text-classification", gold.RootElement.GetProperty("texts").Clone(),
        Parameters: new Dictionary<string, string> { ["truncation"] = "true", ["max_length"] = "128", ["top_k"] = "null" });
    await using (var session = await runtime.CreateSessionAsync(capabilities, token))
    {
        workerPid = supervisor.GetStatus().ProcessId;
        ownedPids.Add(workerPid!.Value);
        response = (await session.InvokeAsync(request, token)).Output.Clone();
        var streamedRows = new List<JsonElement>();
        var terminalCount = 0;
        await foreach (var item in session.InvokeStreamingAsync(request with { Stream = true }, token))
        {
            if (terminalCount != 0) throw new InvalidDataException("Event after terminal");
            if (item.IsTerminal)
            {
                if (item.Event != "done" || item.Data.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Invalid stream terminal");
                terminalCount++;
            }
            else
            {
                if (item.Event != "data") throw new InvalidDataException("Unexpected stream event");
                streamedRows.Add(item.Data.Clone());
            }
        }
        if (terminalCount != 1) throw new InvalidDataException("Missing stream terminal");
        var streamOutput = JsonSerializer.SerializeToElement(streamedRows);
        report["streamOutput"] = streamOutput;
        report["streamComparison"] = Compare(streamOutput, gold.RootElement);
        report["streamDataEvents"] = streamedRows.Count;
        report["streamTerminalEvents"] = terminalCount;
    }
    report["output"] = response;
    var comparison = Compare(response, gold.RootElement);
    report["maximumProbabilityAbsoluteError"] = comparison.MaximumProbabilityAbsoluteError;
    report["labelMatches"] = comparison.LabelMatches; report["sampleCount"] = 8;
    using (var crashed = Process.GetProcessById(workerPid!.Value))
    {
        crashed.Kill(entireProcessTree: true);
        await crashed.WaitForExitAsync(token);
        // A process reopened by PID is not a child handle on every platform;
        // observing termination is portable, querying its exit code is not.
        report["crashedProcessStopped"] = crashed.HasExited;
    }
    await using (var recovered = await runtime.CreateSessionAsync(capabilities, token))
    {
        var recoveredPid = supervisor.GetStatus().ProcessId!.Value;
        ownedPids.Add(recoveredPid);
        if (recoveredPid == workerPid.Value) throw new InvalidDataException("Worker process did not change");
        report["recoveredProcessId"] = recoveredPid;
        var recoveredOutput = (await recovered.InvokeAsync(request, token)).Output.Clone();
        report["recoveredOutput"] = recoveredOutput;
        report["recoveredComparison"] = Compare(recoveredOutput, gold.RootElement);
    }
    report["healthAfterUnload"] = (await runtime.CheckHealthAsync(token)).IsHealthy;
    report["deviceDiagnostics"] = supervisor.GetStatus().Diagnostics.Where(line => line.Contains("Device set to use", StringComparison.Ordinal)).ToArray();
    passed = (bool)report["healthAfterUnload"]!;
}
catch (Exception exception)
{
    report["errorType"] = exception.GetType().Name;
    Console.Error.WriteLine(exception.GetType().Name);
}
finally
{
    try
    {
        if (supervisor is not null)
        {
            var current = supervisor.GetStatus().ProcessId;
            if (current.HasValue) ownedPids.Add(current.Value);
            await supervisor.DisposeAsync();
        }
    }
    catch (Exception exception)
    {
        passed = false;
        report["cleanupErrorType"] = exception.GetType().Name;
    }
    var stopped = ownedPids.Count > 0 && ownedPids.All(pid => !IsAlive(pid));
    report["ownedProcessIds"] = ownedPids;
    report["workerProcessId"] = workerPid; report["workerStopped"] = stopped;
    report["status"] = passed && stopped ? "passed" : "failed";
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { status = report["status"], report = Path.Combine(outputDirectory, "report.json") }));
}
return passed && (bool)report["workerStopped"]! ? 0 : 1;

static async Task<string> HashAsync(string path, CancellationToken token)
{
    await using var file = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
}
static Comparison Compare(JsonElement response, JsonElement gold)
{
    var labels = gold.GetProperty("labels").EnumerateArray().Select(e => e.GetString()!).ToArray();
    var expected = gold.GetProperty("probabilities");
    if (response.GetArrayLength() != expected.GetArrayLength() || expected.GetArrayLength() != 8) throw new InvalidDataException("Sample count mismatch");
    var maxError = 0.0; var matches = 0;
    for (var i = 0; i < response.GetArrayLength(); i++)
    {
        var row = response[i].EnumerateArray().ToDictionary(e => e.GetProperty("label").GetString()!, e => e.GetProperty("score").GetDouble());
        if (row.Count != labels.Length || row.Values.Any(v => !double.IsFinite(v) || v < 0 || v > 1)) throw new InvalidDataException("Invalid probability output");
        for (var j = 0; j < labels.Length; j++) maxError = Math.Max(maxError, Math.Abs(row[labels[j]] - expected[i][j].GetDouble()));
        if (row.MaxBy(p => p.Value).Key == gold.GetProperty("predictedLabels")[i].GetString()) matches++;
    }
    if (matches != 8 || maxError > 0.0001) throw new InvalidDataException("Gold comparison failed");
    return new(maxError, matches);
}
static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
internal sealed record Comparison(double MaximumProbabilityAbsoluteError, int LabelMatches);
