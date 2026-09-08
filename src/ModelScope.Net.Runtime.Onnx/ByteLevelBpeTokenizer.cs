using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelScope.Net.Runtime.Onnx;

/// <summary>GPT-2 compatible byte-level BPE tokenizer backed by vocab.json and merges.txt.</summary>
public sealed class ByteLevelBpeTokenizer
{
    private static readonly Regex Pieces = new(
        "(?i:'s|'t|'re|'ve|'m|'ll|'d)| ?\\p{L}+| ?\\p{N}+| ?[^\\s\\p{L}\\p{N}]+|\\s+(?!\\S)|\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, int> _vocabulary;
    private readonly IReadOnlyDictionary<int, string> _tokens;
    private readonly IReadOnlyDictionary<(string Left, string Right), int> _merges;
    private readonly char[] _byteEncoder;
    private readonly IReadOnlyDictionary<char, byte> _byteDecoder;
    private readonly ConcurrentDictionary<string, string[]> _cache = new(StringComparer.Ordinal);

    private ByteLevelBpeTokenizer(Dictionary<string, int> vocabulary,
        Dictionary<(string Left, string Right), int> merges)
    {
        _vocabulary = vocabulary;
        _tokens = vocabulary.ToDictionary(item => item.Value, item => item.Key);
        _merges = merges;
        (_byteEncoder, _byteDecoder) = CreateByteAlphabet();
    }

    public static ByteLevelBpeTokenizer Load(string vocabularyPath, string mergesPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(vocabularyPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("BPE vocabulary must be an object.");
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Value.TryGetInt32(out var id) || id < 0 || !vocabulary.TryAdd(property.Name, id))
                throw new InvalidDataException("BPE vocabulary contains an invalid entry.");
        }
        if (vocabulary.Count is 0 or > 1_000_000 || vocabulary.Values.Distinct().Count() != vocabulary.Count)
            throw new InvalidDataException("BPE vocabulary size or token identifiers are invalid.");
        var merges = new Dictionary<(string, string), int>();
        foreach (var line in File.ReadLines(mergesPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var separator = line.IndexOf(' ');
            if (separator <= 0 || separator == line.Length - 1 || line.IndexOf(' ', separator + 1) >= 0)
                throw new InvalidDataException("BPE merge entry is invalid.");
            merges.TryAdd((line[..separator], line[(separator + 1)..]), merges.Count);
            if (merges.Count > 1_000_000) throw new InvalidDataException("BPE merge table is oversized.");
        }
        return new(vocabulary, merges);
    }

    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<int>();
        foreach (Match match in Pieces.Matches(text))
        {
            var encoded = string.Concat(Encoding.UTF8.GetBytes(match.Value).Select(value => _byteEncoder[value]));
            foreach (var token in ApplyMerges(encoded))
            {
                if (!_vocabulary.TryGetValue(token, out var id))
                    throw new InvalidDataException("BPE output token is absent from the vocabulary.");
                result.Add(id);
            }
        }
        return result.ToArray();
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        ArgumentNullException.ThrowIfNull(tokenIds);
        var bytes = new List<byte>();
        foreach (var id in tokenIds)
            bytes.AddRange(DecodeTokenBytes(id));
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    public byte[] DecodeTokenBytes(int tokenId)
    {
        if (!_tokens.TryGetValue(tokenId, out var token)) throw new InvalidDataException("BPE token identifier is unknown.");
        var bytes = new byte[token.Length];
        for (var index = 0; index < token.Length; index++)
            if (!_byteDecoder.TryGetValue(token[index], out bytes[index]))
                throw new InvalidDataException("BPE token contains an unknown byte symbol.");
        return bytes;
    }

    private string[] ApplyMerges(string value) => _cache.GetOrAdd(value, token =>
    {
        var symbols = token.Select(character => character.ToString()).ToList();
        while (symbols.Count > 1)
        {
            (string Left, string Right)? selected = null; var selectedRank = int.MaxValue;
            for (var index = 0; index < symbols.Count - 1; index++)
            {
                var pair = (symbols[index], symbols[index + 1]);
                if (_merges.TryGetValue(pair, out var rank) && rank < selectedRank) { selected = pair; selectedRank = rank; }
            }
            if (!selected.HasValue) break;
            var merged = new List<string>(symbols.Count);
            for (var index = 0; index < symbols.Count;)
            {
                if (index < symbols.Count - 1 && symbols[index] == selected.Value.Left && symbols[index + 1] == selected.Value.Right)
                { merged.Add(symbols[index] + symbols[index + 1]); index += 2; }
                else { merged.Add(symbols[index]); index++; }
            }
            symbols = merged;
        }
        return symbols.ToArray();
    });

    private static (char[] Encoder, IReadOnlyDictionary<char, byte> Decoder) CreateByteAlphabet()
    {
        var values = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToList();
        var symbols = values.ToList(); var extra = 0;
        for (var value = 0; value < 256; value++)
            if (!values.Contains(value)) { values.Add(value); symbols.Add(256 + extra++); }
        var encoder = new char[256]; var decoder = new Dictionary<char, byte>();
        for (var index = 0; index < values.Count; index++)
        { encoder[values[index]] = (char)symbols[index]; decoder[(char)symbols[index]] = (byte)values[index]; }
        return (encoder, decoder);
    }
}
