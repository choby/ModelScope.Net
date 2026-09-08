using System.Text.Json;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class OnnxImageClassificationRuntimeTests
{
    private const string ClassificationModelBase64 =
        "CAo6rwIKQgoMcGl4ZWxfdmFsdWVzEgRtZWFuIgpSZWR1Y2VNZWFuKg8KBGF4ZXNAAUACQAOgAQcqDwoIa2VlcGRpbXMYAKABAgooCgRtZWFuEghwb3NpdGl2ZSIJVW5zcXVlZXplKgsKBGF4ZXNAAaABBwoZCghwb3NpdGl2ZRIIbmVnYXRpdmUiA05lZwoxCghuZWdhdGl2ZQoIcG9zaXRpdmUSBmxvZ2l0cyIGQ29uY2F0KgsKBGF4aXMYAaABAhIadmlzaW9uLWNsYXNzaWZpY2F0aW9uLWdvbGRaNgoMcGl4ZWxfdmFsdWVzEiYKJAgBEiAKBxIFYmF0Y2gKAggDCggSBmhlaWdodAoHEgV3aWR0aGIdCgZsb2dpdHMSEwoRCAESDQoHEgViYXRjaAoCCAJCBAoAEAs=";
    private const string RedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mP8z0AaYGIY1TCqYdhqAABALgEfFO4+AgAAAABJRU5ErkJggg==";

    [Fact]
    public async Task Base64ResizeNormalizeSoftmaxAndLabels_MatchNumericalGold()
    {
        var directory = CreateModelDirectory(includePreprocessor: true);
        try
        {
            var runtime = new OnnxImageClassificationRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxImageClassificationOptions { TopK = 2 });
            Assert.Equal(CapabilityStatus.Compatible, runtime.Evaluate(CreateCapabilities(directory)).Status);

            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            var response = await session.InvokeAsync(new ModelRequest(
                "image-classification",
                JsonSerializer.SerializeToElement(new { image = $"data:image/png;base64,{RedPngBase64}" })));

            Assert.Equal("onnx-image-classification", response.Runtime);
            Assert.Equal(1, response.Output.GetProperty("count").GetInt32());
            Assert.Equal(2, response.Output.GetProperty("labelCount").GetInt32());
            Assert.Equal(-1f / 3f, response.Output.GetProperty("logits")[0][0].GetSingle(), precision: 6);
            Assert.Equal(1f / 3f, response.Output.GetProperty("logits")[0][1].GetSingle(), precision: 6);
            Assert.Equal("BRIGHT", response.Output.GetProperty("predictions")[0].GetProperty("label").GetString());
            Assert.Equal(2, response.Output.GetProperty("predictions")[0].GetProperty("scores").GetArrayLength());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsImagesAbovePixelLimit()
    {
        var directory = CreateModelDirectory(includePreprocessor: true);
        try
        {
            var runtime = new OnnxImageClassificationRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxImageClassificationOptions { MaxSourcePixels = 1 });
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));

            var error = await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(new ModelRequest(
                "image-classification",
                JsonSerializer.SerializeToElement(new { image = RedPngBase64 }))));

            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsOversizedBase64BeforeImageDecode()
    {
        var directory = CreateModelDirectory(includePreprocessor: true);
        try
        {
            var runtime = new OnnxImageClassificationRuntime(
                new OnnxRuntimeAdapter(),
                new OnnxImageClassificationOptions { MaxEncodedImageBytes = 3 });
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));

            var error = await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(new ModelRequest(
                "image-classification",
                JsonSerializer.SerializeToElement(new { image = RedPngBase64 }))));

            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
            Assert.Contains("3 bytes", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_RequiresProcessorAndRejectsTextArchitecture()
    {
        var directory = CreateModelDirectory(includePreprocessor: false);
        try
        {
            var runtime = new OnnxImageClassificationRuntime(new OnnxRuntimeAdapter());
            var missing = runtime.Evaluate(CreateCapabilities(directory));
            var textCapabilities = CreateCapabilities(directory) with
            {
                Task = "text-classification",
                Architectures = ["BertForSequenceClassification"],
            };
            var wrongTask = runtime.Evaluate(textCapabilities);

            Assert.Equal(CapabilityStatus.Unavailable, missing.Status);
            Assert.Equal(CapabilityStatus.Unsupported, wrongTask.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/image-classification",
        "fixture-v1",
        "image-classification",
        ["MobileNetV2ForImageClassification"],
        [new ModelArtifact("model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(directory, "model.onnx")).Length)],
        [new RuntimeCandidate("onnx", CapabilityStatus.Compatible, "test", 100)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(bool includePreprocessor)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-image-classification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Convert.FromBase64String(ClassificationModelBase64));
        File.WriteAllText(Path.Combine(directory, "config.json"), """
            { "id2label": { "0": "DARK", "1": "BRIGHT" } }
            """);
        if (includePreprocessor)
        {
            File.WriteAllText(Path.Combine(directory, "preprocessor_config.json"), """
                {
                  "crop_size": { "height": 2, "width": 2 },
                  "do_center_crop": true,
                  "do_normalize": true,
                  "do_rescale": true,
                  "do_resize": true,
                  "image_mean": [0.0, 0.0, 0.0],
                  "image_std": [1.0, 1.0, 1.0],
                  "resample": 2,
                  "rescale_factor": 0.00392156862745098,
                  "size": { "shortest_edge": 2 }
                }
                """);
        }
        return directory;
    }
}
