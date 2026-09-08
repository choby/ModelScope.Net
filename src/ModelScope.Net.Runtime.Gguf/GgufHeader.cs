using System.Buffers.Binary;
using System.Text;

namespace ModelScope.Net.Runtime.Gguf;

public sealed record GgufReaderLimits
{
    public ulong MaxMetadataEntries { get; init; } = 1_000_000;

    public ulong MaxArrayElements { get; init; } = 2_000_000;

    public long MaxMetadataBytes { get; init; } = 64 * 1024 * 1024;

    public int MaxStringBytes { get; init; } = 16 * 1024 * 1024;

    public int MaxArrayNesting { get; init; } = 4;
}

public sealed record GgufHeader(uint Version, ulong TensorCount, ulong MetadataCount)
{
    public string? Architecture { get; init; }

    public uint? FileType { get; init; }

    public string? FileTypeName => FileType switch
    {
        0 => "F32",
        1 => "F16",
        2 => "Q4_0",
        3 => "Q4_1",
        7 => "Q8_0",
        8 => "Q5_0",
        9 => "Q5_1",
        10 => "Q2_K",
        11 => "Q3_K_S",
        12 => "Q3_K_M",
        13 => "Q3_K_L",
        14 => "Q4_K_S",
        15 => "Q4_K_M",
        16 => "Q5_K_S",
        17 => "Q5_K_M",
        18 => "Q6_K",
        _ => FileType is null ? null : $"UNKNOWN_{FileType.Value}",
    };

    public uint? QuantizationVersion { get; init; }

    public string? TokenizerModel { get; init; }

    public string? ChatTemplate { get; init; }

    public bool HasChatTemplate { get; init; }

    public long MetadataEndOffset { get; init; }

    public static Task<GgufHeader> ReadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ReadAsync(path, new GgufReaderLimits(), cancellationToken);

    public static async Task<GgufHeader> ReadAsync(
        string path,
        GgufReaderLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxMetadataEntries == 0 ||
            limits.MaxArrayElements == 0 ||
            limits.MaxMetadataBytes < 24 ||
            limits.MaxStringBytes < 1 ||
            limits.MaxArrayNesting < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "GGUF reader limits must be positive.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var reader = new GgufBinaryReader(stream, limits);
            var magic = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            if (!magic.Span.SequenceEqual("GGUF"u8))
                throw new InvalidDataException("The file does not start with the GGUF magic.");

            var version = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            if (version is not (2 or 3))
                throw new InvalidDataException($"GGUF version {version} is not supported; versions 2 and 3 are recognized.");
            var tensorCount = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
            var metadataCount = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
            if (metadataCount > limits.MaxMetadataEntries)
                throw new InvalidDataException($"GGUF metadata count exceeds the {limits.MaxMetadataEntries} entry limit.");

            reader.BeginMetadata();
            string? architecture = null;
            uint? fileType = null;
            uint? quantizationVersion = null;
            string? tokenizerModel = null;
            string? chatTemplate = null;
            var hasChatTemplate = false;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            for (ulong index = 0; index < metadataCount; index++)
            {
                var key = await reader.ReadStringAsync(
                    capture: true,
                    maximumBytes: ushort.MaxValue,
                    cancellationToken).ConfigureAwait(false) ?? string.Empty;
                ValidateKey(key);
                if (!seenKeys.Add(key))
                    throw new InvalidDataException($"GGUF metadata key '{key}' is duplicated.");
                var valueType = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var capture = key is "general.architecture" or
                    "general.file_type" or
                    "general.quantization_version" or
                    "tokenizer.ggml.model" or
                    "tokenizer.chat_template";
                var value = await reader.ReadValueAsync(
                    valueType,
                    capture,
                    depth: 0,
                    cancellationToken).ConfigureAwait(false);

                switch (key)
                {
                    case "general.architecture":
                        architecture = RequireType<string>(key, valueType, value, GgufValueType.String);
                        break;
                    case "general.file_type":
                        fileType = RequireType<uint>(key, valueType, value, GgufValueType.UInt32);
                        break;
                    case "general.quantization_version":
                        quantizationVersion = RequireType<uint>(key, valueType, value, GgufValueType.UInt32);
                        break;
                    case "tokenizer.ggml.model":
                        tokenizerModel = RequireType<string>(key, valueType, value, GgufValueType.String);
                        break;
                    case "tokenizer.chat_template":
                        hasChatTemplate = true;
                        chatTemplate = RequireType<string>(key, valueType, value, GgufValueType.String);
                        break;
                }
            }

            return new GgufHeader(version, tensorCount, metadataCount)
            {
                Architecture = architecture,
                FileType = fileType,
                QuantizationVersion = quantizationVersion,
                TokenizerModel = tokenizerModel,
                ChatTemplate = chatTemplate,
                HasChatTemplate = hasChatTemplate,
                MetadataEndOffset = stream.Position,
            };
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The GGUF header or metadata is truncated.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The GGUF metadata contains an overflowing length.", exception);
        }
    }

    private static T RequireType<T>(
        string key,
        uint actualType,
        object? value,
        GgufValueType expectedType)
    {
        if ((GgufValueType)actualType != expectedType || value is not T typed)
            throw new InvalidDataException($"GGUF metadata '{key}' must use type {expectedType}.");
        return typed;
    }

    private static void ValidateKey(string key)
    {
        var segments = key.Split('.');
        if (segments.Any(segment => segment.Length == 0 || segment.Any(character =>
            !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
        {
            throw new InvalidDataException("GGUF metadata keys must be non-empty hierarchical ASCII identifiers.");
        }
    }

    private enum GgufValueType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }

    private sealed class GgufBinaryReader(FileStream stream, GgufReaderLimits limits)
    {
        private readonly byte[] _numberBuffer = new byte[8];
        private long _metadataStart = -1;

        public void BeginMetadata() => _metadataStart = stream.Position;

        public async ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(
            int count,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            EnsureMetadataBudget();
            return buffer;
        }

        public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(_numberBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            EnsureMetadataBudget();
            return BinaryPrimitives.ReadUInt32LittleEndian(_numberBuffer);
        }

        public async ValueTask<ulong> ReadUInt64Async(CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(_numberBuffer, cancellationToken).ConfigureAwait(false);
            EnsureMetadataBudget();
            return BinaryPrimitives.ReadUInt64LittleEndian(_numberBuffer);
        }

        public async ValueTask<string?> ReadStringAsync(
            bool capture,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            var length = await ReadUInt64Async(cancellationToken).ConfigureAwait(false);
            var allowed = Math.Min(maximumBytes, limits.MaxStringBytes);
            if (length > (ulong)allowed)
                throw new InvalidDataException($"GGUF string length exceeds the {allowed} byte limit.");
            if (!capture)
            {
                Skip(length);
                return null;
            }
            var bytes = await ReadBytesAsync(checked((int)length), cancellationToken).ConfigureAwait(false);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("GGUF metadata contains invalid UTF-8.", exception);
            }
        }

        public async ValueTask<object?> ReadValueAsync(
            uint rawType,
            bool capture,
            int depth,
            CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined((GgufValueType)rawType))
                throw new InvalidDataException($"GGUF metadata value type {rawType} is not recognized.");
            var type = (GgufValueType)rawType;
            if (type == GgufValueType.Array)
            {
                if (depth >= limits.MaxArrayNesting)
                    throw new InvalidDataException($"GGUF array nesting exceeds the {limits.MaxArrayNesting} level limit.");
                var elementType = await ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                var count = await ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                if (count > limits.MaxArrayElements)
                    throw new InvalidDataException($"GGUF array length exceeds the {limits.MaxArrayElements} element limit.");
                await SkipArrayAsync(elementType, count, depth + 1, cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (type == GgufValueType.String)
                return await ReadStringAsync(capture, limits.MaxStringBytes, cancellationToken).ConfigureAwait(false);

            var size = FixedSize(type);
            if (!capture)
            {
                Skip((ulong)size);
                return null;
            }
            await stream.ReadExactlyAsync(_numberBuffer.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            EnsureMetadataBudget();
            return type switch
            {
                GgufValueType.UInt8 => _numberBuffer[0],
                GgufValueType.Int8 => unchecked((sbyte)_numberBuffer[0]),
                GgufValueType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(_numberBuffer),
                GgufValueType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(_numberBuffer),
                GgufValueType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(_numberBuffer),
                GgufValueType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(_numberBuffer),
                GgufValueType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_numberBuffer)),
                GgufValueType.Bool when _numberBuffer[0] is 0 or 1 => _numberBuffer[0] == 1,
                GgufValueType.Bool => throw new InvalidDataException("GGUF boolean metadata must be encoded as 0 or 1."),
                GgufValueType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(_numberBuffer),
                GgufValueType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(_numberBuffer),
                GgufValueType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_numberBuffer)),
                _ => throw new InvalidDataException($"GGUF metadata value type {rawType} is not supported."),
            };
        }

        private async ValueTask SkipArrayAsync(
            uint rawElementType,
            ulong count,
            int depth,
            CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined((GgufValueType)rawElementType))
                throw new InvalidDataException($"GGUF array element type {rawElementType} is not recognized.");
            var elementType = (GgufValueType)rawElementType;
            if (elementType is not (GgufValueType.String or GgufValueType.Array))
            {
                Skip(checked(count * (ulong)FixedSize(elementType)));
                return;
            }
            for (ulong index = 0; index < count; index++)
            {
                await ReadValueAsync(rawElementType, capture: false, depth, cancellationToken).ConfigureAwait(false);
            }
        }

        private static int FixedSize(GgufValueType type) => type switch
        {
            GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
            GgufValueType.UInt16 or GgufValueType.Int16 => 2,
            GgufValueType.UInt32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
            GgufValueType.UInt64 or GgufValueType.Int64 or GgufValueType.Float64 => 8,
            _ => throw new InvalidDataException($"GGUF metadata type {type} has no fixed size."),
        };

        private void Skip(ulong byteCount)
        {
            if (byteCount > long.MaxValue || byteCount > (ulong)(stream.Length - stream.Position))
                throw new EndOfStreamException();
            stream.Seek(checked((long)byteCount), SeekOrigin.Current);
            EnsureMetadataBudget();
        }

        private void EnsureMetadataBudget()
        {
            if (_metadataStart >= 0 && stream.Position - _metadataStart > limits.MaxMetadataBytes)
                throw new InvalidDataException($"GGUF metadata exceeds the {limits.MaxMetadataBytes} byte limit.");
        }
    }
}
