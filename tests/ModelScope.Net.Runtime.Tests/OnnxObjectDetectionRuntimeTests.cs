using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxObjectDetectionRuntimeTests
{
    private const string DetectionModelBase64 =
        "CAgyFW9iamVjdC1kZXRlY3Rpb24tZ29sZDqIAwo+CgZpbWFnZXMSBG1lYW4iClJlZHVjZU1lYW4qEQoEYXhlc0AAQAFAAkADoAEHKg8KCGtlZXBkaW1zGACgAQIKGAoEbWVhbgoFemVyb3MSBGJpYXMiA011bAogCgpkZXRlY3Rpb25zCgRiaWFzEgdvdXRwdXQwIgNBZGQSDHlvbG8tZml4dHVyZSpgCAEIAQgDCAYQAUIKZGV0ZWN0aW9uc0pIAAAAQAAAAEAAAABAAAAAQGZmZj/NzEw/ZmYGQGZmBkAAAABAAAAAQGZmZj8zMzM/AAAAPwAAAD8AAIA/AACAP83MzD3NzMw9KlkIAQgDCAYQAUIFemVyb3NKSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFogCgZpbWFnZXMSFgoUCAESEAoCCAEKAggDCgIIBAoCCARiHQoHb3V0cHV0MBISChAIARIMCgIIAQoCCAMKAggGQgQKABAL";
    private const string RedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mP8z0AaYGIY1TCqYdhqAABALgEfFO4+AgAAAABJRU5ErkJggg==";

    [Fact]
    public async Task LetterboxYolov5Nms_KeepsHighScoreBoxAndMapsToSourcePixels()
    {
        var directory = CreateModelDirectory();
        try
        {
            var runtime = new OnnxObjectDetectionRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxObjectDetectionOptions
                {
                    InputHeight = 4,
                    InputWidth = 4,
                    ConfidenceThreshold = 0.25f,
                    IouThreshold = 0.45f,
                });
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(CreateCapabilities(directory)).Status);

            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            var response = await session.InvokeAsync(new ModelRequest(
                "object-detection",
                JsonSerializer.SerializeToElement(new { image = $"data:image/png;base64,{RedPngBase64}" })));

            Assert.Equal("onnx-object-detection", response.Runtime);
            Assert.Equal(1, response.Output.GetProperty("count").GetInt32());
            Assert.Equal("person", response.Output.GetProperty("labels")[0].GetString());
            Assert.Equal(0.72f, response.Output.GetProperty("scores")[0].GetSingle(), precision: 5);
            var box = response.Output.GetProperty("boxes")[0];
            Assert.Equal(4f, box[0].GetSingle(), precision: 4);
            Assert.Equal(4f, box[1].GetSingle(), precision: 4);
            Assert.Equal(12f, box[2].GetSingle(), precision: 4);
            Assert.Equal(12f, box[3].GetSingle(), precision: 4);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsImagesAbovePixelLimit()
    {
        var directory = CreateModelDirectory();
        try
        {
            var runtime = new OnnxObjectDetectionRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxObjectDetectionOptions { InputHeight = 4, InputWidth = 4, MaxSourcePixels = 1 });
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(new ModelRequest(
                "object-detection",
                JsonSerializer.SerializeToElement(new { image = RedPngBase64 }))));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RequiresDetectionTask()
    {
        var directory = CreateModelDirectory();
        try
        {
            var runtime = new OnnxObjectDetectionRuntime(new OnnxRuntimeAdapter());
            var wrongTask = runtime.Evaluate(CreateCapabilities(directory) with
            {
                Task = "image-classification",
                Architectures = ["MobileNetV2ForImageClassification"],
            });
            Assert.Equal(CapabilityStatus.Unsupported, wrongTask.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/object-detection",
        "fixture-v1",
        "object-detection",
        ["YoloForObjectDetection"],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-object-detection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Convert.FromBase64String(DetectionModelBase64));
        File.WriteAllText(Path.Combine(directory, "config.json"), """
            { "id2label": { "0": "person" } }
            """);
        return directory;
    }
}
