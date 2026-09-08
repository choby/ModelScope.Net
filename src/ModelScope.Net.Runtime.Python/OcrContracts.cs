using System.Buffers.Binary;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Python;

public sealed record OcrImageInput(ReadOnlyMemory<byte> Data, string MimeType = "image/png")
{
    internal ModelRequest ToRequest(string task)
    {
        if (Data.IsEmpty || Data.Length > 8 * 1024 * 1024 || MimeType is not ("image/png" or "image/jpeg"))
            throw InvalidInput();
        var bytes = Data.Span;
        if (MimeType == "image/png")
        {
            if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw InvalidInput();
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
            if (width is 0 or > 8192 || height is 0 or > 8192 || (ulong)width * height > 16UL * 1024 * 1024)
                throw InvalidInput();
        }
        else if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
            throw InvalidInput();
        return new ModelRequest(task, JsonSerializer.SerializeToElement(new
        {
            image = new { mimeType = MimeType, data = Convert.ToBase64String(bytes) },
        }));
    }

    private static ModelScopeException InvalidInput() => new(ModelScopeErrorCode.InvalidRequest,
        "OCR requires one supported inline image within the configured byte and pixel limits.");
}

public sealed record OcrRecognitionRequest(OcrImageInput Image)
{
    public const string TaskName = "ocr-recognition";
    public ModelRequest ToModelRequest() => (Image ?? throw new ArgumentNullException(nameof(Image))).ToRequest(TaskName);
}

public sealed record OcrDetectionRequest(OcrImageInput Image)
{
    public const string TaskName = "ocr-detection";
    public ModelRequest ToModelRequest() => (Image ?? throw new ArgumentNullException(nameof(Image))).ToRequest(TaskName);
}

public sealed record OcrRecognitionResult(IReadOnlyList<string> Texts, ModelResponse Response)
{
    public string Text => Texts.Count == 1 ? Texts[0] : throw new InvalidOperationException("A single OCR result was expected.");

    public static OcrRecognitionResult FromResponse(ModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var value = response.Output.GetProperty("text");
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 1) throw InvalidOutput();
            var text = value[0].GetString();
            if (text is null || text.Length > 65536) throw InvalidOutput();
            return new(Array.AsReadOnly(new[] { text }), response);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw InvalidOutput();
        }
    }
    private static ModelScopeException InvalidOutput() => new(ModelScopeErrorCode.InferenceFailed,
        "The OCR recognition response is invalid or oversized.");
}

public sealed record OcrPolygon(IReadOnlyList<double> Coordinates);

public sealed record OcrDetectionResult(IReadOnlyList<OcrPolygon> Polygons, ModelResponse Response)
{
    public static OcrDetectionResult FromResponse(ModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var value = response.Output.GetProperty("polygons");
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 10000) throw InvalidOutput();
            var polygons = new List<OcrPolygon>();
            var total = 0;
            foreach (var polygon in value.EnumerateArray())
            {
                if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() is < 8 or > 64 || polygon.GetArrayLength() % 2 != 0 ||
                    (total += polygon.GetArrayLength()) > 80000) throw InvalidOutput();
                var coordinates = polygon.EnumerateArray().Select(item => item.GetDouble()).ToArray();
                if (coordinates.Any(number => !double.IsFinite(number))) throw InvalidOutput();
                polygons.Add(new OcrPolygon(Array.AsReadOnly(coordinates)));
            }
            return new(Array.AsReadOnly(polygons.ToArray()), response);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidOutput();
        }
    }
    private static ModelScopeException InvalidOutput() => new(ModelScopeErrorCode.InferenceFailed,
        "The OCR detection response is invalid or oversized.");
}

public static class OcrSessionExtensions
{
    public static async Task<OcrRecognitionResult> RecognizeTextAsync(this IModelSession session,
        OcrRecognitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return OcrRecognitionResult.FromResponse(await session.InvokeAsync(request.ToModelRequest(), cancellationToken).ConfigureAwait(false));
    }

    public static async Task<OcrDetectionResult> DetectTextAsync(this IModelSession session,
        OcrDetectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return OcrDetectionResult.FromResponse(await session.InvokeAsync(request.ToModelRequest(), cancellationToken).ConfigureAwait(false));
    }
}
