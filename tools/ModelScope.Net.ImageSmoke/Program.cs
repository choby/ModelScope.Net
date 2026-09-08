using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Python;

if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: ImageSmoke NET_ROOT ORIGINAL_SOURCE_ROOT [SAMPLE_INDEX]");
var root = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var sampleIndex = args.Length == 3 ? int.Parse(args[2]) : 0;
using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/compatibility/image-generation-plan.json")));
var sample = plan.RootElement.GetProperty("samples")[sampleIndex];
var fullDrill = sampleIndex == 0;
var snapshot = Path.Combine(root, "artifacts/image-generation/sd15-snapshot");
var output = Path.Combine(root, "artifacts/image-generation/dotnet-runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
Console.WriteLine(output);
var report = new Dictionary<string, object?> { ["status"] = "failed", ["scope"] = fullDrill ? "single .NET image stream/recovery drill; pixel parity checked separately" : "single .NET image sample; pixel parity checked separately", ["sampleId"] = sample.GetProperty("id").GetString() };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
await using var supervisor = new LocalPythonWorkerSupervisor(new()
{
    PythonExecutable = Path.Combine(root, ".certification-venv/bin/python"),
    WorkerScript = Path.Combine(root, "worker/python/server.py"), WorkingDirectory = root,
    Arguments = { "--port", port.ToString(), "--backend", "modelscope", "--workers", "1", "--max-sessions", "1" },
    AllowedModelRoots = { snapshot },
    Environment =
    {
        ["PYTHONPATH"] = string.Join(Path.PathSeparator, Path.Combine(root, ".image-model-deps"), Path.Combine(root, ".worker-model-deps"), source),
        ["MODELSCOPE_CACHE"] = Path.Combine(output, "cache"), ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
    },
});
try
{
    using var http = new HttpClient();
    using var runtime = new GrpcPythonWorkerRuntime(http, new()
    {
        Endpoint = new Uri($"http://127.0.0.1:{port}"), Transport = PythonWorkerTransport.Grpc,
        RequestTimeout = TimeSpan.FromMinutes(3), RecoveryAttempts = 20,
    }, supervisor);
    var caps = (await new ModelInspector().InspectAsync(snapshot, timeout.Token)) with
    {
        ModelId = "AI-ModelScope/stable-diffusion-v1-5",
        Revision = "50b8f071f400a9a501706bb7530481056f5f0558",
    };
    var request = new ImageGenerationRequest(sample.GetProperty("prompt").GetString()!,
        Width: sample.GetProperty("width").GetInt32(), Height: sample.GetProperty("height").GetInt32(),
        Steps: sample.GetProperty("steps").GetInt32(), GuidanceScale: sample.GetProperty("guidanceScale").GetDouble(),
        Seed: sample.GetProperty("seed").GetInt64());
    report["request"] = request;
    int originalPid;
    byte[] png;
    await using (var session = await runtime.CreateSessionAsync(caps, timeout.Token))
    {
    originalPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("Worker PID unavailable");
    var result = await session.GenerateImagesAsync(request, timeout.Token);
    if (result.Images.Count != 1 || result.Images[0].Width != request.Width || result.Images[0].Height != request.Height)
        throw new InvalidDataException("Unexpected image dimensions");
    png = result.Images[0].PngData.ToArray();
    await File.WriteAllBytesAsync(Path.Combine(output, "dotnet.png"), png, timeout.Token);
    report["pngSha256"] = Convert.ToHexStringLower(SHA256.HashData(png));
    report["modelId"] = result.Response.ModelId; report["revision"] = result.Response.Revision;
    report["runtime"] = result.Response.Runtime; report["elapsedSeconds"] = result.Response.Elapsed.TotalSeconds;
    if (fullDrill)
    {
    var dataEvents = 0;
    var terminalEvents = 0;
    byte[]? streamedPng = null;
    await foreach (var item in session.InvokeStreamingAsync(request.ToModelRequest() with { Stream = true }, timeout.Token))
    {
        if (terminalEvents != 0) throw new InvalidDataException("Event after stream terminal");
        if (item.IsTerminal)
        {
            if (item.Event != "done" || item.Data.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Invalid stream terminal");
            terminalEvents++;
        }
        else
        {
            if (item.Event != "data" || ++dataEvents != 1)
                throw new InvalidDataException("Unexpected image stream data event");
            var decoded = ImageGenerationResult.FromResponse(result.Response with { Output = item.Data.Clone() });
            if (decoded.Images.Count != 1 || decoded.Images[0].Width != request.Width || decoded.Images[0].Height != request.Height)
                throw new InvalidDataException("Unexpected stream image shape");
            streamedPng = decoded.Images[0].PngData.ToArray();
        }
    }
    if (dataEvents != 1 || terminalEvents != 1 || streamedPng is null)
        throw new InvalidDataException("Incomplete image stream");
    await File.WriteAllBytesAsync(Path.Combine(output, "stream.png"), streamedPng, timeout.Token);
    report["streamDataEvents"] = dataEvents; report["streamTerminalEvents"] = terminalEvents;
    report["streamPngSha256"] = Convert.ToHexStringLower(SHA256.HashData(streamedPng));
    report["repeatEncodedBytesMatch"] = png.AsSpan().SequenceEqual(streamedPng);
    if (!png.AsSpan().SequenceEqual(streamedPng)) throw new InvalidDataException("Repeated seeded image differed");
    }
    }
    if (fullDrill)
    {
    using (var crashed = Process.GetProcessById(originalPid))
    {
        crashed.Kill(entireProcessTree: true);
        await crashed.WaitForExitAsync(timeout.Token);
        report["crashedProcessStopped"] = crashed.HasExited;
    }
    await using (var recovered = await runtime.CreateSessionAsync(caps, timeout.Token))
    {
        var recoveredPid = supervisor.GetStatus().ProcessId ?? throw new InvalidOperationException("Recovered PID unavailable");
        if (recoveredPid == originalPid) throw new InvalidDataException("Worker process did not change");
        report["originalProcessId"] = originalPid; report["recoveredProcessId"] = recoveredPid;
        var recoveredResult = await recovered.GenerateImagesAsync(request, timeout.Token);
        var recoveredPng = recoveredResult.Images.Single().PngData.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(output, "recovered.png"), recoveredPng, timeout.Token);
        report["recoveredPngSha256"] = Convert.ToHexStringLower(SHA256.HashData(recoveredPng));
        report["recoveredBytesMatch"] = png.AsSpan().SequenceEqual(recoveredPng);
        if (!png.AsSpan().SequenceEqual(recoveredPng)) throw new InvalidDataException("Recovered image differed");
    }
    }
    report["status"] = "passed";
}
finally
{
    await supervisor.StopAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
