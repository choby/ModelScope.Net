using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxRuntimeAdapterTests
{
    private const string AddModelBase64 =
        "CAoSFE1vZGVsU2NvcGUuTmV0LlRlc3RzOlgKEAoBeAoBeRIDc3VtIgNBZGQSD2FkZC10d28tdmVjdG9yc1oPCgF4EgoKCAgBEgQKAggCWg8KAXkSCgoICAESBAoCCAJiEQoDc3VtEgoKCAgBEgQKAggCQgQKABAN";

    [Fact]
    public async Task CpuSession_RunsFloatTensorsAndReturnsShapeMetadata()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            var runtime = new OnnxRuntimeAdapter();
            var capabilities = CreateCapabilities(directory);
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(capabilities).Status);

            await using var session = await runtime.CreateSessionAsync(capabilities);
            using var payload = JsonDocument.Parse("""
                { "inputs": { "x": [1.25, 2.5], "y": [2.75, 3.5] } }
                """);
            var response = await session.InvokeAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone()));

            var output = response.Output.GetProperty("outputs").GetProperty("sum");
            Assert.Equal([2], output.GetProperty("shape").EnumerateArray().Select(item => item.GetInt32()).ToArray());
            Assert.Equal([4f, 6f], output.GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray());
            Assert.Equal("Single", output.GetProperty("type").GetString());
            Assert.Equal("onnx", response.Runtime);

            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(new ModelRequest("raw-onnx", payload.RootElement.Clone(), Stream: true)))
            {
                events.Add(item);
            }

            Assert.Single(events);
            Assert.True(events[0].IsTerminal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CpuSession_RejectsMissingInputAndWrongShape()
    {
        var directory = CreateModelDirectory(validModel: true);
        try
        {
            await using var session = await new OnnxRuntimeAdapter().CreateSessionAsync(CreateCapabilities(directory));
            using var missing = JsonDocument.Parse("""{ "x": [1, 2] }""");
            var missingException = await Assert.ThrowsAsync<ModelScopeException>(
                () => session.InvokeAsync(new ModelRequest("raw-onnx", missing.RootElement.Clone())));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, missingException.Code);

            using var wrongShape = JsonDocument.Parse("""{ "x": [1, 2, 3], "y": [4, 5, 6] }""");
            var shapeException = await Assert.ThrowsAsync<ModelScopeException>(
                () => session.InvokeAsync(new ModelRequest("raw-onnx", wrongShape.RootElement.Clone())));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, shapeException.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_MapsInvalidModelToStructuredLoadError()
    {
        var directory = CreateModelDirectory(validModel: false);
        try
        {
            var exception = await Assert.ThrowsAsync<ModelScopeException>(
                () => new OnnxRuntimeAdapter().CreateSessionAsync(CreateCapabilities(directory)));
            Assert.Equal(ModelScopeErrorCode.ModelLoadFailed, exception.Code);
            Assert.True(exception.IsRetryable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/add-vectors",
        "fixture-v1",
        "raw-onnx",
        [],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(bool validModel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-onnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "model.onnx"),
            validModel ? Convert.FromBase64String(AddModelBase64) : [1, 2, 3, 4]);
        return directory;
    }
}
