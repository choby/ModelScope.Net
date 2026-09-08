using System.Text.Json;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public class OcrContractsTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    private static OcrImageInput Image() => new(Convert.FromBase64String(Png));
    private static ModelResponse Response(object value) => new(JsonSerializer.SerializeToElement(value),
        "model", "commit", "python", TimeSpan.Zero);

    [Fact]
    public void MapsRecognitionAndDetectionInlineImageRequests()
    {
        foreach (var request in new[] { new OcrRecognitionRequest(Image()).ToModelRequest(), new OcrDetectionRequest(Image()).ToModelRequest() })
        {
            Assert.StartsWith("ocr-", request.Task);
            Assert.Equal("image/png", request.Payload.GetProperty("image").GetProperty("mimeType").GetString());
            Assert.Equal(Png, request.Payload.GetProperty("image").GetProperty("data").GetString());
            Assert.Null(request.Parameters);
        }
    }

    [Fact]
    public void RejectsUnsupportedOrMalformedImageTransport()
    {
        OcrImageInput[] invalid = [new(ReadOnlyMemory<byte>.Empty), new(new byte[8]),
            new(Convert.FromBase64String(Png), "image/gif"), new(new byte[] { 0xff, 0xd8, 0, 0 }, "image/jpeg")];
        foreach (var image in invalid)
            Assert.Equal(ModelScopeErrorCode.InvalidRequest,
                Assert.Throws<ModelScopeException>(() => new OcrRecognitionRequest(image).ToModelRequest()).Code);
        Assert.Throws<ArgumentNullException>(() => new OcrDetectionRequest(null!).ToModelRequest());
    }

    [Fact]
    public void ParsesRecognitionAndPreservesResponse()
    {
        var response = Response(new { text = new[] { "模型Scope" } });
        var result = OcrRecognitionResult.FromResponse(response);
        Assert.Equal("模型Scope", result.Text);
        Assert.Same(response, result.Response);
    }

    [Fact]
    public void ParsesDetectionCoordinatesAndPreservesResponse()
    {
        var response = Response(new { polygons = new[] { new[] { 0, 0, 10, 0, 10, 5, 0, 5 } } });
        var result = OcrDetectionResult.FromResponse(response);
        Assert.Equal(new double[] { 0, 0, 10, 0, 10, 5, 0, 5 }, Assert.Single(result.Polygons).Coordinates);
        Assert.Same(response, result.Response);
    }

    [Fact]
    public void RejectsMalformedOrOversizedOutputs()
    {
        foreach (var value in new object[] { new { }, new { text = 1 }, new { text = Array.Empty<string>() },
                     new { text = new[] { "a", "b" } }, new { text = new[] { new string('x', 65537) } } })
            Assert.Equal(ModelScopeErrorCode.InferenceFailed,
                Assert.Throws<ModelScopeException>(() => OcrRecognitionResult.FromResponse(Response(value))).Code);
        foreach (var value in new object[] { new { }, new { polygons = new[] { new[] { 0, 1 } } },
                     new { polygons = new[] { new object[] { 0, 0, 1, 0, 1, 1, 0, "bad" } } } })
            Assert.Equal(ModelScopeErrorCode.InferenceFailed,
                Assert.Throws<ModelScopeException>(() => OcrDetectionResult.FromResponse(Response(value))).Code);
    }
}
