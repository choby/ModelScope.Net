using System.Text.Json;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public class ImageGenerationResultTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    private static ModelResponse Response(object output) => new(JsonSerializer.SerializeToElement(output),
        "model", "commit", "python", TimeSpan.Zero);
    private static object Item(string data = Png, int width = 1, int height = 1, string mimeType = "image/png") =>
        new { data, width, height, mimeType };

    [Fact]
    public void ParsesBytesAndPreservesProvenance()
    {
        var response = Response(new { images = new[] { Item() } });
        var result = ImageGenerationResult.FromResponse(response);
        Assert.Same(response, result.Response);
        var image = Assert.Single(result.Images);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(Convert.FromBase64String(Png), image.PngData.ToArray());
    }

    [Fact]
    public void RejectsMalformedAndOversizedTransport()
    {
        object[] invalid = [new { }, new { images = Array.Empty<object>() },
            new { images = Enumerable.Repeat(Item(), 5).ToArray() },
            new { images = new[] { Item("bad-base64") } },
            new { images = new[] { Item(width: 2) } },
            new { images = new[] { Item(height: 0) } },
            new { images = new[] { Item(mimeType: "image/jpeg") } },
            new { images = new[] { Item(new string('A', 3 * 1024 * 1024)) } },
            new { images = new[] { new { data = Png } } },
            new { images = new[] { Item(width: int.MaxValue, height: int.MaxValue) } }];
        foreach (var value in invalid)
            Assert.Equal(ModelScopeErrorCode.InferenceFailed,
                Assert.Throws<ModelScopeException>(() => ImageGenerationResult.FromResponse(Response(value))).Code);
    }
}
