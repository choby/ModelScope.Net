using System.Buffers.Binary;
using System.Text.Json;

namespace ModelScope.Net.Runtime.Python;

public sealed record SpeechRecognitionRequest(ReadOnlyMemory<byte> Audio)
{
    public const string TaskName = "auto-speech-recognition";

    public ModelRequest ToModelRequest()
    {
        ValidateWave(Audio.Span);
        return new(TaskName, JsonSerializer.SerializeToElement(new
        {
            audio = new { mimeType = "audio/wav", data = Convert.ToBase64String(Audio.Span) },
        }));
    }

    private static void ValidateWave(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 44 or > 20 * 1024 * 1024 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
            throw InvalidInput();
        var offset = 12; var formatSeen = false; var dataBytes = 0;
        while (offset <= bytes.Length - 8)
        {
            var id = bytes.Slice(offset, 4); var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            offset += 8;
            if (length > int.MaxValue || (long)offset + length > bytes.Length) throw InvalidInput();
            var chunk = bytes.Slice(offset, (int)length);
            if (id.SequenceEqual("fmt "u8))
            {
                if (formatSeen || chunk.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2)) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4)) != 16000 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(8, 4)) != 32000 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(12, 2)) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2)) != 16) throw InvalidInput();
                formatSeen = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (dataBytes != 0 || length == 0 || length % 2 != 0 || length > 16000U * 2 * 600) throw InvalidInput();
                dataBytes = (int)length;
            }
            offset += checked((int)length + ((int)length & 1));
        }
        if (!formatSeen || dataBytes == 0 || offset != bytes.Length) throw InvalidInput();
    }

    private static ModelScopeException InvalidInput() => new(ModelScopeErrorCode.InvalidRequest,
        "Speech recognition requires one inline mono 16 kHz PCM16 WAV of at most ten minutes.");
}

public sealed record SpeechRecognitionResult(string Text, ModelResponse Response)
{
    public static SpeechRecognitionResult FromResponse(ModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var text = response.Output.GetProperty("text").GetString();
            if (text is null || text.Length > 65536) throw InvalidOutput();
            return new(text, response);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw InvalidOutput();
        }
    }

    private static ModelScopeException InvalidOutput() => new(ModelScopeErrorCode.InferenceFailed,
        "The speech recognition response is invalid or oversized.");
}

public static class SpeechRecognitionSessionExtensions
{
    public static async Task<SpeechRecognitionResult> RecognizeSpeechAsync(this IModelSession session,
        SpeechRecognitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return SpeechRecognitionResult.FromResponse(
            await session.InvokeAsync(request.ToModelRequest(), cancellationToken).ConfigureAwait(false));
    }
}
