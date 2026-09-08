using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Onnx;

if (args.Length != 2) throw new ArgumentException("Usage: OnnxGenerationCertification NET_ROOT GOLD_DIRECTORY");
var root = Path.GetFullPath(args[0]); var goldDirectory = Path.GetFullPath(args[1]);
var modelPath = Path.Combine(root, "artifacts/onnx-text-generation/smollm-135m");
var output = Path.Combine(root, "artifacts/onnx-text-generation/dotnet-runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output); Console.WriteLine(output);
var report = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["status"] = "failed",
    ["scope"] = "local macOS ARM64 CPU, fixed quantized SmolLM ONNX greedy generation" };
try
{
    using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(goldDirectory, "report.json")));
    var revision = gold.RootElement.GetProperty("revision").GetString()!;
    var modelId = gold.RootElement.GetProperty("modelId").GetString()!;
    var options = new OnnxTextGenerationOptions { ModelFile = "onnx/model_quantized.onnx", DefaultMaxNewTokens = 8 };
    var runtime = new OnnxTextGenerationRuntime(options);
    var capabilities = (await new ModelInspector().InspectAsync(modelPath)) with
    { ModelId = modelId, Revision = revision, Task = "text-generation", Architectures = ["LlamaForCausalLM"] };
    var candidate = runtime.Evaluate(capabilities);
    if (candidate.Status is CapabilityStatus.Unsupported or CapabilityStatus.Unavailable) throw new InvalidDataException(candidate.Reason);
    var tokenizer = ByteLevelBpeTokenizer.Load(Path.Combine(modelPath, "vocab.json"), Path.Combine(modelPath, "merges.txt"));
    var results = new List<object>();
    await using var session = await runtime.CreateSessionAsync(capabilities);
    foreach (var expected in gold.RootElement.GetProperty("results").EnumerateArray())
    {
        var prompt = expected.GetProperty("prompt").GetString()!;
        var expectedPromptTokens = expected.GetProperty("promptTokenIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (!tokenizer.Encode(prompt).SequenceEqual(expectedPromptTokens)) throw new InvalidDataException("BPE prompt tokens differ from Python");
        var response = await session.InvokeAsync(new("text-generation", JsonSerializer.SerializeToElement(new { prompt, maxNewTokens = 8 })));
        var actualTokens = response.Output.GetProperty("tokenIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var expectedTokens = expected.GetProperty("tokenIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var expectedText = expected.GetProperty("text").GetString();
        if (!actualTokens.SequenceEqual(expectedTokens) || response.Output.GetProperty("text").GetString() != expectedText)
            throw new InvalidDataException("ONNX generation differs from Python gold");
        results.Add(new { prompt, passed = true, tokenIds = actualTokens, text = expectedText, response.Runtime });
    }
    var first = gold.RootElement.GetProperty("results")[0]; var firstPrompt = first.GetProperty("prompt").GetString()!;
    var expectedFirstTokens = first.GetProperty("tokenIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
    var repeat = await session.InvokeAsync(new("text-generation", JsonSerializer.SerializeToElement(new { prompt = firstPrompt, maxNewTokens = 8 })));
    if (!repeat.Output.GetProperty("tokenIds").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(expectedFirstTokens))
        throw new InvalidDataException("Repeated greedy generation was not deterministic");
    var streamTokens = new List<int>(); var streamText = new StringBuilder(); var done = 0;
    await foreach (var item in session.InvokeStreamingAsync(new("text-generation", JsonSerializer.SerializeToElement(new { prompt = firstPrompt, maxNewTokens = 8 }), Stream: true)))
    {
        if (item.IsTerminal) { if (item.Event != "done" || item.Data.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Invalid terminal event"); done++; }
        else { if (done != 0 || item.Event != "data") throw new InvalidDataException("Invalid stream event order");
            streamTokens.Add(item.Data.GetProperty("tokenId").GetInt32()); streamText.Append(item.Data.GetProperty("text").GetString()); }
    }
    if (done != 1 || !streamTokens.SequenceEqual(expectedFirstTokens) || streamText.ToString() != first.GetProperty("generatedText").GetString())
        throw new InvalidDataException("Streaming generation differs from Python gold");
    report["modelId"] = modelId; report["revision"] = revision; report["candidateStatus"] = candidate.Status.ToString();
    report["results"] = results; report["repeat"] = new { passed = true, tokenIds = expectedFirstTokens };
    report["stream"] = new { passed = true, dataEvents = streamTokens.Count, doneEvents = done, tokenIds = streamTokens, generatedText = streamText.ToString() };
    report["modelSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(modelPath, "onnx/model_quantized.onnx"))));
    report["goldReportSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(goldDirectory, "report.json"))));
    report["status"] = "passed";
}
catch (Exception error)
{
    report["errorType"] = error.GetType().Name; report["error"] = error.Message; throw;
}
finally
{
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
