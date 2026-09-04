using System.Text;
using System.Text.Json;

namespace BetterTranslator.Core.Verification.Checks.Semantics;

public sealed record TokenizedText(IReadOnlyList<int> Ids, IReadOnlyList<string> Pieces);

public sealed class UnigramTokenizer
{
    private const char Metaspace = '▁';

    private readonly Dictionary<string, (int Id, double Score)> _vocabulary;

    private readonly int _maxPieceLength;

    private readonly int _unknownId;

    private readonly int _beginId;

    private readonly int _endId;

    private readonly int _maxTokens;

    private UnigramTokenizer(Dictionary<string, (int, double)> vocabulary, int unknownId, int beginId, int endId, int maxTokens)
    {
        _vocabulary = vocabulary;
        _maxPieceLength = vocabulary.Keys.Max(k => k.Length);
        _unknownId = unknownId;
        _beginId = beginId;
        _endId = endId;
        _maxTokens = maxTokens;
    }

    public int VocabularySize => _vocabulary.Count;

    public int BeginId => _beginId;

    public int EndId => _endId;

    public static UnigramTokenizer Load(string tokenizerJsonPath, int maxTokens = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerJsonPath);

        return Parse(File.ReadAllText(tokenizerJsonPath), maxTokens);
    }

    public static UnigramTokenizer Parse(string tokenizerJson, int maxTokens = 512)
    {
        ArgumentNullException.ThrowIfNull(tokenizerJson);

        using var document = JsonDocument.Parse(tokenizerJson);
        var model = document.RootElement.GetProperty("model");
        var type = model.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (!string.Equals(type, "Unigram", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"tokenizer model '{type}' is not supported; the embedding host reads Unigram tokenizers only");
        }

        var vocabulary = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
        var index = 0;

        foreach (var entry in model.GetProperty("vocab").EnumerateArray())
        {
            var piece = entry[0].GetString()!;
            var score = entry[1].GetDouble();
            vocabulary.TryAdd(piece, (index, score));
            index++;
        }

        var unknownId = model.TryGetProperty("unk_id", out var unk) ? unk.GetInt32() : 0;
        var beginId = SpecialId(document.RootElement, "<s>", 0);
        var endId = SpecialId(document.RootElement, "</s>", 2);

        return new UnigramTokenizer(vocabulary, unknownId, beginId, endId, maxTokens);
    }

    public TokenizedText Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ids = new List<int> { _beginId };
        var pieces = new List<string> { "<s>" };
        var normalized = Normalize(text);

        foreach (var piece in Segment(normalized))
        {
            if (ids.Count >= _maxTokens - 1)
            {
                break;
            }

            ids.Add(_vocabulary.TryGetValue(piece, out var entry) ? entry.Id : _unknownId);
            pieces.Add(piece);
        }

        ids.Add(_endId);
        pieces.Add("</s>");

        return new TokenizedText(ids, pieces);
    }

    public static string Normalize(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalized = collapsed.Normalize(NormalizationForm.FormKC);

        return Metaspace + normalized.Replace(' ', Metaspace);
    }

    private IEnumerable<string> Segment(string text)
    {
        var length = text.Length;
        var best = new double[length + 1];
        var back = new int[length + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0;

        for (var end = 1; end <= length; end++)
        {
            var longest = Math.Min(_maxPieceLength, end);

            for (var size = 1; size <= longest; size++)
            {
                var start = end - size;

                if (double.IsNegativeInfinity(best[start]))
                {
                    continue;
                }

                var piece = text.Substring(start, size);
                double score;

                if (_vocabulary.TryGetValue(piece, out var entry))
                {
                    score = entry.Score;
                }
                else if (size == 1)
                {
                    score = UnknownPenalty;
                }
                else
                {
                    continue;
                }

                var candidate = best[start] + score;

                if (candidate > best[end])
                {
                    best[end] = candidate;
                    back[end] = start;
                }
            }
        }

        var boundaries = new List<int>();

        for (var at = length; at > 0; at = back[at])
        {
            boundaries.Add(at);
        }

        boundaries.Reverse();
        var previous = 0;

        foreach (var boundary in boundaries)
        {
            yield return text[previous..boundary];
            previous = boundary;
        }
    }

    private const double UnknownPenalty = -100;

    private static int SpecialId(JsonElement root, string content, int fallback)
    {
        if (!root.TryGetProperty("added_tokens", out var added))
        {
            return fallback;
        }

        foreach (var token in added.EnumerateArray())
        {
            if (string.Equals(token.GetProperty("content").GetString(), content, StringComparison.Ordinal))
            {
                return token.GetProperty("id").GetInt32();
            }
        }

        return fallback;
    }
}
