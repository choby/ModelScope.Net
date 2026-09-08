using System.Globalization;
using System.Text;

namespace ModelScope.Net.Runtime.Onnx;

internal sealed record WordPieceTokenizerSettings(
    int MaxLength,
    bool Lowercase,
    string PadToken,
    string UnknownToken,
    string ClassToken,
    string SeparatorToken);

internal sealed record WordPieceTokenBatch(
    long[][] InputIds,
    long[][] AttentionMask,
    long[][] TokenTypeIds);

internal sealed class WordPieceTokenizer
{
    private readonly IReadOnlyDictionary<string, long> _vocabulary;
    private readonly WordPieceTokenizerSettings _settings;
    private readonly long _pad;
    private readonly long _unknown;
    private readonly long _classification;
    private readonly long _separator;

    private WordPieceTokenizer(
        IReadOnlyDictionary<string, long> vocabulary,
        WordPieceTokenizerSettings settings)
    {
        _vocabulary = vocabulary;
        _settings = settings;
        _pad = RequireToken(settings.PadToken);
        _unknown = RequireToken(settings.UnknownToken);
        _classification = RequireToken(settings.ClassToken);
        _separator = RequireToken(settings.SeparatorToken);
    }

    public static WordPieceTokenizer Load(string path, WordPieceTokenizerSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxLength, 2);
        var vocabulary = File.ReadLines(path)
            .Select((token, index) => (token, index))
            .Where(item => !string.IsNullOrEmpty(item.token))
            .ToDictionary(item => item.token, item => (long)item.index, StringComparer.Ordinal);
        return new WordPieceTokenizer(vocabulary, settings);
    }

    public WordPieceTokenBatch Encode(IReadOnlyList<string> texts)
    {
        var sequences = texts.Select(EncodeOne).ToArray();
        var width = sequences.Max(sequence => sequence.Count);
        var ids = new long[sequences.Length][];
        var masks = new long[sequences.Length][];
        var types = new long[sequences.Length][];
        for (var row = 0; row < sequences.Length; row++)
        {
            ids[row] = Enumerable.Repeat(_pad, width).ToArray();
            masks[row] = new long[width];
            types[row] = new long[width];
            for (var column = 0; column < sequences[row].Count; column++)
            {
                ids[row][column] = sequences[row][column];
                masks[row][column] = 1;
            }
        }

        return new WordPieceTokenBatch(ids, masks, types);
    }

    private List<long> EncodeOne(string text)
    {
        var result = new List<long>(_settings.MaxLength) { _classification };
        foreach (var token in BasicTokenize(text))
        {
            foreach (var piece in SplitWordPiece(token))
            {
                if (result.Count >= _settings.MaxLength - 1) break;
                result.Add(piece);
            }
            if (result.Count >= _settings.MaxLength - 1) break;
        }
        result.Add(_separator);
        return result;
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        var normalized = _settings.Lowercase
            ? text.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            : text;
        var buffer = new StringBuilder();
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (_settings.Lowercase && category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(character) || IsCjk(character) || char.IsPunctuation(character))
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                if (!char.IsWhiteSpace(character)) yield return character.ToString();
            }
            else
            {
                buffer.Append(character);
            }
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private IEnumerable<long> SplitWordPiece(string token)
    {
        if (_vocabulary.TryGetValue(token, out var exact)) return [exact];
        if (token.Length > 100) return [_unknown];
        var result = new List<long>();
        var start = 0;
        while (start < token.Length)
        {
            long? found = null;
            var foundEnd = start;
            for (var end = token.Length; end > start; end--)
            {
                var candidate = (start == 0 ? string.Empty : "##") + token[start..end];
                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    found = id;
                    foundEnd = end;
                    break;
                }
            }
            if (found is null) return [_unknown];
            result.Add(found.Value);
            start = foundEnd;
        }
        return result;
    }

    private long RequireToken(string token) => _vocabulary.TryGetValue(token, out var id)
        ? id
        : throw new ModelScopeException(
            ModelScopeErrorCode.ModelLoadFailed,
            $"Required tokenizer token '{token}' is missing from the vocabulary.");

    private static bool IsCjk(char value) =>
        value is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}
