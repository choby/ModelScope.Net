using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Python;

if (args.Length != 3) throw new ArgumentException("Usage: AsrCertification NET_ROOT ORIGINAL_SOURCE_ROOT GOLD_DIRECTORY");
var root = Path.GetFullPath(args[0]); var source = Path.GetFullPath(args[1]); var gold = Path.GetFullPath(args[2]);
var output = Path.Combine(root, "artifacts/asr/dotnet-runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output); Console.WriteLine(output);
var report = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["status"] = "failed",
    ["scope"] = "local macOS CPU fixed Paraformer ASR exact-text comparison" };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var modelPath = Path.Combine(root, "artifacts/asr/paraformer-snapshot");
await using var supervisor = new LocalPythonWorkerSupervisor(new()
{
    PythonExecutable = Path.Combine(root, ".certification-venv/bin/python"), WorkerScript = Path.Combine(root, "worker/python/server.py"),
    WorkingDirectory = root, Arguments = { "--port", port.ToString(), "--backend", "modelscope", "--workers", "1", "--max-sessions", "1" },
    AllowedModelRoots = { modelPath }, Security = { CaptureDiagnostics = true },
    Environment =
    {
        ["PYTHONPATH"] = string.Join(Path.PathSeparator, Path.Combine(root, ".asr-model-deps"), Path.Combine(root, ".ocr-model-deps"),
            Path.Combine(root, ".image-model-deps"), Path.Combine(root, ".worker-model-deps"), source),
        ["MODELSCOPE_CACHE"] = Path.Combine(output, "cache"), ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
    },
});
try
{
    using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(gold, "report.json"), timeout.Token));
    var expected = goldDocument.RootElement.GetProperty("output").GetProperty("text").GetString()!;
    var revision = goldDocument.RootElement.GetProperty("revision").GetString()!;
    var modelId = goldDocument.RootElement.GetProperty("modelId").GetString()!;
    var capabilities = (await new ModelInspector().InspectAsync(modelPath, timeout.Token)) with { ModelId = modelId, Revision = revision };
    using var http = new HttpClient();
    using var runtime = new GrpcPythonWorkerRuntime(http, new() { Endpoint = new($"http://127.0.0.1:{port}"),
        Transport = PythonWorkerTransport.Grpc, RequestTimeout = TimeSpan.FromMinutes(2), RecoveryAttempts = 80,
        RecoveryDelay = TimeSpan.FromMilliseconds(500) }, supervisor);
    var input = new SpeechRecognitionRequest(await File.ReadAllBytesAsync(Path.Combine(modelPath, "example/asr_example.wav"), timeout.Token));
    await using (var session = await runtime.CreateSessionAsync(capabilities, timeout.Token))
    {
        var result = await session.RecognizeSpeechAsync(input, timeout.Token);
        if (result.Text != expected) throw new InvalidDataException("ASR exact-text mismatch");
        var streamed = SpeechRecognitionResult.FromResponse(result.Response with
        { Output = await ReadSingleDataAsync(session.InvokeStreamingAsync(input.ToModelRequest() with { Stream = true }, timeout.Token), timeout.Token) });
        if (streamed.Text != expected) throw new InvalidDataException("Streamed ASR exact-text mismatch");
        report["ordinary"] = new { passed = true, result.Text, result.Response.ModelId, result.Response.Revision };
        report["stream"] = new { passed = true, events = "one-data-one-done", streamed.Text };
    }
    var originalPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("ASR worker PID unavailable");
    using (var process = Process.GetProcessById(originalPid)) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token); }
    await using (var session = await runtime.CreateSessionAsync(capabilities, timeout.Token))
    {
        var recovered = await session.RecognizeSpeechAsync(input, timeout.Token);
        if (recovered.Text != expected) throw new InvalidDataException("Recovered ASR exact-text mismatch");
        var recoveredPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("Recovered ASR worker PID unavailable");
        if (recoveredPid == originalPid) throw new InvalidDataException("ASR worker process did not recover");
        report["recovery"] = new { passed = true, originalProcessId = originalPid, recoveredProcessId = recoveredPid, recovered.Text };
    }
    report["inputSha256"] = Convert.ToHexStringLower(SHA256.HashData(input.Audio.Span));
    report["goldReportSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(gold, "report.json"), timeout.Token)));
    report["transportSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "worker/python/audio_transport.py"), timeout.Token)));
    report["status"] = "passed";
}
catch (Exception error)
{
    report["errorType"] = error.GetType().Name; report["error"] = error.Message;
    var status = supervisor.GetStatus(); report["workerRunning"] = status.IsRunning; report["workerProcessId"] = status.ProcessId;
    report["workerExitCode"] = status.LastExitCode; report["workerDiagnostics"] = status.Diagnostics.TakeLast(30).ToArray();
    throw;
}
finally
{
    await supervisor.StopAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

static async Task<JsonElement> ReadSingleDataAsync(IAsyncEnumerable<ModelStreamEvent> events, CancellationToken token)
{
    JsonElement? data = null; var terminal = 0;
    await foreach (var item in events.WithCancellation(token))
    {
        if (terminal != 0) throw new InvalidDataException("Event after ASR stream terminal");
        if (item.IsTerminal) { if (item.Event != "done" || item.Data.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Invalid ASR terminal"); terminal++; }
        else { if (item.Event != "data" || data.HasValue) throw new InvalidDataException("Unexpected ASR data event"); data = item.Data.Clone(); }
    }
    if (!data.HasValue || terminal != 1) throw new InvalidDataException("Incomplete ASR stream");
    return data.Value;
}
