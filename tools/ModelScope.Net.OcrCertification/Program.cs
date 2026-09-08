using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Python;

if (args.Length != 2) throw new ArgumentException("Usage: OcrCertification NET_ROOT ORIGINAL_SOURCE_ROOT");
var root = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var gold = Path.Combine(root, "artifacts/ocr/python-runs/42b3fe987c334b36bdc6c980f8510c3d");
var output = Path.Combine(root, "artifacts/ocr/dotnet-runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output); Console.WriteLine(output);
var report = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["status"] = "failed",
    ["scope"] = "local macOS CPU fixed OCR recognition and detection exact-output comparison" };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var recognitionPath = Path.Combine(root, "artifacts/ocr/recognition-snapshot");
var detectionPath = Path.Combine(root, "artifacts/ocr/detection-snapshot");
await using var supervisor = new LocalPythonWorkerSupervisor(new()
{
    PythonExecutable = Path.Combine(root, ".certification-venv/bin/python"),
    WorkerScript = Path.Combine(root, "worker/python/server.py"), WorkingDirectory = root,
    Arguments = { "--port", port.ToString(), "--backend", "modelscope", "--workers", "1", "--max-sessions", "1" },
    AllowedModelRoots = { recognitionPath, detectionPath },
    Security = { CaptureDiagnostics = true },
    Environment =
    {
        ["PYTHONPATH"] = string.Join(Path.PathSeparator, Path.Combine(root, ".ocr-model-deps"), Path.Combine(root, ".image-model-deps"), Path.Combine(root, ".worker-model-deps"), source),
        ["MODELSCOPE_CACHE"] = Path.Combine(output, "cache"), ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
    },
});
try
{
    using var http = new HttpClient();
    using var runtime = new GrpcPythonWorkerRuntime(http, new() { Endpoint = new($"http://127.0.0.1:{port}"),
        Transport = PythonWorkerTransport.Grpc, RequestTimeout = TimeSpan.FromMinutes(2), RecoveryAttempts = 60,
        RecoveryDelay = TimeSpan.FromMilliseconds(500) }, supervisor);
    var recognitionCaps = (await new ModelInspector().InspectAsync(recognitionPath, timeout.Token)) with
    { ModelId = "damo/cv_convnextTiny_ocr-recognition-general_damo", Revision = "6346468feced662993f8a79f7f62004dc534cca6" };
    using var recognitionGold = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(gold, "recognition.json"), timeout.Token));
    var expectedText = recognitionGold.RootElement.GetProperty("output").GetProperty("text")[0].GetString()!;
    await using (var session = await runtime.CreateSessionAsync(recognitionCaps, timeout.Token))
    {
        var input = new OcrImageInput(await File.ReadAllBytesAsync(Path.Combine(root, "artifacts/ocr/inputs/recognition.png"), timeout.Token));
        var result = await session.RecognizeTextAsync(new(input), timeout.Token);
        if (result.Text != expectedText) throw new InvalidDataException("OCR recognition mismatch");
        var streamed = await ReadSingleDataAsync(session.InvokeStreamingAsync(new OcrRecognitionRequest(input).ToModelRequest() with { Stream = true }, timeout.Token), timeout.Token);
        var streamResult = OcrRecognitionResult.FromResponse(result.Response with { Output = streamed });
        if (streamResult.Text != expectedText) throw new InvalidDataException("Streamed OCR recognition mismatch");
        report["recognition"] = new { passed = true, result.Text, result.Response.ModelId, result.Response.Revision,
            streamEvents = "one-data-one-done", inputSha256 = Convert.ToHexStringLower(SHA256.HashData(input.Data.Span)) };
    }
    var originalPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("OCR worker PID unavailable");
    using (var crashed = Process.GetProcessById(originalPid))
    {
        crashed.Kill(entireProcessTree: true); await crashed.WaitForExitAsync(timeout.Token);
        report["crashedProcessStopped"] = crashed.HasExited;
    }
    var detectionCaps = (await new ModelInspector().InspectAsync(detectionPath, timeout.Token)) with
    { ModelId = "damo/cv_resnet18_ocr-detection-db-line-level_damo", Revision = "3a6b98fc046f99e8ec97d4ed25f478909a123253" };
    using var detectionGold = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(gold, "detection.json"), timeout.Token));
    var expected = detectionGold.RootElement.GetProperty("output").GetProperty("polygons").EnumerateArray()
        .Select(row => row.EnumerateArray().Select(value => value.GetDouble()).ToArray()).ToArray();
    await using (var session = await runtime.CreateSessionAsync(detectionCaps, timeout.Token))
    {
        var input = new OcrImageInput(await File.ReadAllBytesAsync(Path.Combine(root, "artifacts/ocr/inputs/detection.png"), timeout.Token));
        var result = await session.DetectTextAsync(new(input), timeout.Token);
        if (result.Polygons.Count != expected.Length || result.Polygons.Where((polygon, index) => !polygon.Coordinates.SequenceEqual(expected[index])).Any())
            throw new InvalidDataException("OCR detection mismatch");
        var recoveredPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("Recovered OCR worker PID unavailable");
        if (recoveredPid == originalPid) throw new InvalidDataException("OCR worker process did not recover");
        var streamed = await ReadSingleDataAsync(session.InvokeStreamingAsync(new OcrDetectionRequest(input).ToModelRequest() with { Stream = true }, timeout.Token), timeout.Token);
        var streamResult = OcrDetectionResult.FromResponse(result.Response with { Output = streamed });
        if (streamResult.Polygons.Count != expected.Length || streamResult.Polygons.Where((polygon, index) => !polygon.Coordinates.SequenceEqual(expected[index])).Any())
            throw new InvalidDataException("Streamed OCR detection mismatch");
        report["detection"] = new { passed = true, polygonCount = result.Polygons.Count, result.Response.ModelId,
            result.Response.Revision, streamEvents = "one-data-one-done", inputSha256 = Convert.ToHexStringLower(SHA256.HashData(input.Data.Span)) };
        report["originalProcessId"] = originalPid; report["recoveredProcessId"] = recoveredPid;
    }
    report["workerSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "worker/python/server.py"), timeout.Token)));
    report["transportSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "worker/python/ocr_transport.py"), timeout.Token)));
    report["goldReportSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(gold, "report.json"), timeout.Token)));
    report["status"] = "passed";
}
catch (Exception error)
{
    report["errorType"] = error.GetType().Name;
    var workerStatus = supervisor.GetStatus();
    report["workerRunning"] = workerStatus.IsRunning; report["workerProcessId"] = workerStatus.ProcessId;
    report["workerExitCode"] = workerStatus.LastExitCode;
    report["workerDiagnostics"] = workerStatus.Diagnostics.TakeLast(20).ToArray();
    throw;
}
finally
{
    await supervisor.StopAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

static async Task<JsonElement> ReadSingleDataAsync(IAsyncEnumerable<ModelStreamEvent> events, CancellationToken token)
{
    JsonElement? data = null;
    var terminal = 0;
    await foreach (var item in events.WithCancellation(token))
    {
        if (terminal != 0) throw new InvalidDataException("Event after OCR stream terminal");
        if (item.IsTerminal)
        {
            if (item.Event != "done" || item.Data.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Invalid OCR stream terminal");
            terminal++;
        }
        else
        {
            if (item.Event != "data" || data.HasValue) throw new InvalidDataException("Unexpected OCR stream data event");
            data = item.Data.Clone();
        }
    }
    if (!data.HasValue || terminal != 1) throw new InvalidDataException("Incomplete OCR stream");
    return data.Value;
}
