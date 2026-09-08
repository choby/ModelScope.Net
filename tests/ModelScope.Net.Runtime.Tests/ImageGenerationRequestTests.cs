using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public class ImageGenerationRequestTests
{
    [Fact]
    public void SeedPreservesSigned64BitRange()
    {
        foreach (var seed in new long[] { 0, 42, long.MaxValue })
            Assert.Equal(seed, new ImageGenerationRequest("cube", Seed: seed)
                .ToModelRequest().Payload.GetProperty("seed").GetInt64());
        Assert.Equal(ModelScopeErrorCode.InvalidRequest,
            Assert.Throws<ModelScopeException>(() => new ImageGenerationRequest("cube", Seed: -1)
                .ToModelRequest()).Code);
    }

    [Fact]
    public void MapsOriginalStableDiffusionInputContract()
    {
        var request = new ImageGenerationRequest("a red cube", "blur", 256, 512, 5, 4, 2).ToModelRequest();
        Assert.Equal("text-to-image-synthesis", request.Task);
        Assert.Equal("a red cube", request.Payload.GetProperty("text").GetString());
        Assert.Equal("blur", request.Payload.GetProperty("negative_prompt").GetString());
        Assert.Equal(256, request.Payload.GetProperty("width").GetInt32());
        Assert.Equal(512, request.Payload.GetProperty("height").GetInt32());
        Assert.Equal(5, request.Payload.GetProperty("num_inference_steps").GetInt32());
        Assert.Equal(4, request.Payload.GetProperty("guidance_scale").GetDouble());
        Assert.Equal(2, request.Payload.GetProperty("num_images_per_prompt").GetInt32());
        Assert.Equal("pil", request.Payload.GetProperty("output_type").GetString());
        Assert.True(request.Payload.GetProperty("return_dict").GetBoolean());
        Assert.False(request.Stream);
        Assert.Null(request.Parameters);
    }

    [Fact]
    public void RejectsInvalidAndUnboundedInputs()
    {
        var valid = new ImageGenerationRequest("cube");
        ImageGenerationRequest[] invalid = [
            valid with { Prompt = " " }, valid with { Prompt = new string('x', 4097) },
            valid with { NegativePrompt = new string('x', 4097) },
            valid with { Width = 65 }, valid with { Height = 0 }, valid with { Width = 2056 },
            valid with { Count = 0 }, valid with { Count = 5 },
            valid with { Width = 2048, Height = 2048, Count = 2 },
            valid with { Steps = 0 }, valid with { Steps = 101 },
            valid with { GuidanceScale = double.NaN }, valid with { GuidanceScale = double.PositiveInfinity },
            valid with { GuidanceScale = -1 }, valid with { GuidanceScale = 31 },
        ];
        foreach (var input in invalid)
            Assert.Equal(ModelScopeErrorCode.InvalidRequest,
                Assert.Throws<ModelScopeException>(() => input.ToModelRequest()).Code);
    }

    [Fact]
    public void AcceptsInclusiveBounds()
    {
        new ImageGenerationRequest("cube", Width: 2048, Height: 2048, Steps: 100, GuidanceScale: 30).ToModelRequest();
        new ImageGenerationRequest("cube", Width: 64, Height: 64, Steps: 1, GuidanceScale: 0, Count: 4).ToModelRequest();
    }
}
