using System.Globalization;
using System.Text;

namespace BetterTranslator.Indexing.Retrieval;

/// <summary>
/// Lexical retrieval over the indexed chunks.
///
/// This is the half of retrieval that needs no model: it scores on the words
/// themselves, so a project's own wording is findable the moment it is indexed,
/// with or without embeddings.
/// </summary>
public sealed class Bm25Index
{
    // Standard parameters. k1 damps repeated terms, b controls how much a long
    // document is penalised.
    private const double K1 = 1.5;
    private const double B = 0.75;

    private readonly List<Document> _documents = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    private double _averageLength;

    public int Count => _documents.Count;

    public void Add(Guid chunkId, string text)
    {
        var terms = Tokenize(text);
        if (terms.Count == 0)
        {
            return;
        }

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        }

        foreach (var term in frequencies.Keys)
        {
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }

        _documents.Add(new Document(chunkId, frequencies, terms.Count));
        _averageLength = _documents.Average(d => d.Length);
    }

    /// <summary>
    /// Scores every document against the query, best first. Documents that
    /// share no term with the query score zero and are dropped.
    /// </summary>
    public IReadOnlyList<ScoredChunk> Search(string query, int take = 10)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0 || _documents.Count == 0)
        {
            return [];
        }

        var hits = new List<ScoredChunk>();

        foreach (var document in _documents)
        {
            var score = 0.0;

            foreach (var term in terms.Distinct(StringComparer.Ordinal))
            {
                if (!document.Frequencies.TryGetValue(term, out var frequency))
                {
                    continue;
                }

                var containing = _documentFrequency.GetValueOrDefault(term);
                var idf = Math.Log(1 + ((_documents.Count - containing + 0.5) / (containing + 0.5)));

                var normalised = frequency + (K1 * (1 - B + (B * document.Length / _averageLength)));
                score += idf * (frequency * (K1 + 1)) / normalised;
            }

            if (score > 0)
            {
                hits.Add(new ScoredChunk(document.ChunkId, score, RetrievalKind.Lexical));
            }
        }

        return [.. hits.OrderByDescending(h => h.Score).Take(take)];
    }

    /// <summary>
    /// Splits on anything that is not a letter or digit, and lowercases. Keeps
    /// accented letters whole, which matters for the languages this translates
    /// into.
    /// </summary>
    public static List<string> Tokenize(string? text)
    {
        var terms = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return terms;
        }

        var current = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLower(c, CultureInfo.CurrentCulture));
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }

    private sealed record Document(Guid ChunkId, Dictionary<string, int> Frequencies, int Length);
}
