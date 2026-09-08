using System.Text.Json;

namespace ModelScope.Net.Runtime.Python;

/// <summary>Bounded inputs for the ModelScope Stable Diffusion pipeline contract.</summary>
/// <remarks>This validates request shape, not GPU memory availability or model compatibility.</remarks>
public sealed record ImageGenerationRequest(
    string Prompt,
    string? NegativePrompt = null,
    int Width = 512,
    int Height = 512,
    int Steps = 30,
    double GuidanceScale = 7.5,
    int Count = 1,
    long? Seed = null)
{
    public const string TaskName = "text-to-image-synthesis";

    public ModelRequest ToModelRequest()
    {
        if (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > 4096 ||
            NegativePrompt?.Length > 4096 ||
            Width is < 64 or > 2048 || Height is < 64 or > 2048 ||
            Width % 8 != 0 || Height % 8 != 0 || Count is < 1 or > 4 ||
            (long)Width * Height * Count > 4 * 1024 * 1024 ||
            Steps is < 1 or > 100 || !double.IsFinite(GuidanceScale) ||
            GuidanceScale is < 0 or > 30 || Seed is < 0)
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest,
                "Image generation inputs exceed the supported prompt, dimension, count or sampling bounds.");

        return new ModelRequest(TaskName, JsonSerializer.SerializeToElement(new
        {
            text = Prompt,
            negative_prompt = NegativePrompt,
            width = Width,
            height = Height,
            num_inference_steps = Steps,
            guidance_scale = GuidanceScale,
            num_images_per_prompt = Count,
            seed = Seed,
            output_type = "pil",
            return_dict = true,
        }));
    }
}
