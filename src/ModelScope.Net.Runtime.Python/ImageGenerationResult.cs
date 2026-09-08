using System.Buffers.Binary;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Python;

public sealed record GeneratedImage(int Width, int Height, ReadOnlyMemory<byte> PngData);

/// <summary>Bounded PNG transport results. This is not a full PNG decoder or content safety check.</summary>
public sealed record ImageGenerationResult(IReadOnlyList<GeneratedImage> Images, ModelResponse Response)
{
    public static ImageGenerationResult FromResponse(ModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var root = response.Output;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array ||
                images.GetArrayLength() is < 1 or > 4)
                throw InvalidOutput();
            var result = new List<GeneratedImage>();
            long pixels = 0;
            var remaining = 2 * 1024 * 1024;
            foreach (var item in images.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.GetProperty("mimeType").GetString() != "image/png")
                    throw InvalidOutput();
                var width = item.GetProperty("width").GetInt32();
                var height = item.GetProperty("height").GetInt32();
                if (width <= 0 || height <= 0 ||
                    (pixels += (long)width * height) > 4 * 1024 * 1024)
                    throw InvalidOutput();
                var encoded = item.GetProperty("data").GetString();
                if (encoded is null || encoded.Length > ((remaining + 2) / 3) * 4)
                    throw InvalidOutput();
                var png = Convert.FromBase64String(encoded);
                if (png.Length > remaining || png.Length < 33 ||
                    !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 ||
                    !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                    BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != width ||
                    BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != height)
                    throw InvalidOutput();
                remaining -= png.Length;
                result.Add(new GeneratedImage(width, height, png));
            }
            return new ImageGenerationResult(result.AsReadOnly(), response);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidOutput();
        }
    }

    private static ModelScopeException InvalidOutput() => new(ModelScopeErrorCode.InferenceFailed,
        "The image generation response has an invalid or oversized PNG transport payload.");
}

public static class ImageGenerationSessionExtensions
{
    /// <summary>Invoke a compatible image session without taking ownership of it.</summary>
    public static async Task<ImageGenerationResult> GenerateImagesAsync(this IModelSession session,
        ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ImageGenerationResult.FromResponse(await session.InvokeAsync(request.ToModelRequest(), cancellationToken)
            .ConfigureAwait(false));
    }
}
