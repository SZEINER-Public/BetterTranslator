using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Slop;

/// <summary>One technical term the document declared, and how often it appears.</summary>
public sealed record DocumentTerm(string Term, int Count);

/// <summary>A term the document treats two ways at once.</summary>
public sealed record TermInconsistency(string Term, int Kept, int Translated, string Majority, IReadOnlyList<int> KeptAt);

/// <summary>
/// The technical vocabulary of one document, derived from the document. Ported
/// from `Get-DocumentTerms` and `Get-TermInconsistency` in `rag.ps1`.
///
/// There is no finite list of product names, commands and technical nouns, so a
/// hand-written glossary can never be complete -- the term you forget is the one
/// that gets mistranslated. But the infinite set is never needed. Only the words
/// THIS document uses matter, and the document already declares them: a word its
/// author put in backticks is a word its author considered technical.
///
/// Measured on the reference's own README: 4609 prose words, 87 candidates.
/// </summary>
public static class DocumentTerms
{
    /// <summary>
    /// A property of the English language: fixed, universal, the same for every
    /// project. These reach the candidate list through inline spans that are
    /// whole sentences. It is deliberately NOT a per-project terminology list --
    /// keeping that distinction is what means nothing here needs maintaining
    /// when the document changes.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "not", "that", "for", "with", "this", "are", "from", "its", "which", "you", "than", "into",
        "only", "one", "every", "never", "first", "rather", "when", "have", "has", "was", "were", "all", "any",
        "can", "but", "they", "their", "them", "then", "there", "these", "those", "what", "who", "how", "why",
        "where", "while", "would", "could", "should", "will", "shall", "may", "might", "must", "been", "being",
        "does", "did", "done", "doing", "each", "more", "most", "some", "such", "same", "other", "another", "also",
        "just", "even", "still", "yet", "out", "off", "over", "under", "again", "once", "because", "before",
        "after", "above", "below", "between", "through", "during", "without", "within", "about", "against",
        "among", "upon", "onto", "since", "until", "unless", "both", "either", "neither", "nor", "too", "very",
        "own", "via", "use", "used", "uses", "make", "makes", "get", "gets", "see", "say", "says",
    };

    private static readonly Regex InlineCode = new("`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex FencedBlock = new("(?ms)^```.*?^```", RegexOptions.Compiled);
    private static readonly Regex InlineCodeSpan = new("`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex Splitter = new(@"[\s/\\=:,;()]+", RegexOptions.Compiled);
    private static readonly Regex Word = new("[A-Za-z][A-Za-z0-9_-]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Candidates: words the author put in backticks that also appear in the
    /// prose at least <paramref name="minFrequency"/> times.
    ///
    /// Two filters make it usable. Fenced blocks are excluded, because they hold
    /// console output -- itself English sentences -- and including them took the
    /// list from 87 to 612, nearly all of it noise. Then the stopword list
    /// removes "the", "and", "that".
    /// </summary>
    public static IReadOnlyList<DocumentTerm> Find(string text, int minFrequency = 2)
    {
        ArgumentNullException.ThrowIfNull(text);

        var codeWords = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in InlineCode.Matches(text))
        {
            var pieces = Splitter.Split(m.Groups[1].Value).Where(p => p.Length > 0).ToArray();

            // A sentence, not an identifier.
            if (pieces.Length > 5)
            {
                continue;
            }

            foreach (var piece in pieces)
            {
                foreach (Match w in Word.Matches(piece))
                {
                    codeWords.Add(w.Value.ToLowerInvariant());
                }
            }
        }

        var prose = InlineCodeSpan.Replace(FencedBlock.Replace(text, " "), " ");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (Match w in Word.Matches(prose))
        {
            var token = w.Value.ToLowerInvariant();

            if (counts.TryGetValue(token, out var n))
            {
                counts[token] = n + 1;
            }
            else
            {
                counts[token] = 1;
                order.Add(token);
            }
        }

        // Grouped in first-seen order and then sorted by count descending, which
        // is what Group-Object followed by Sort-Object -Descending produces:
        // .NET's OrderByDescending is stable, so ties keep that order.
        return
        [
            .. order
                .Where(t => counts[t] >= minFrequency && codeWords.Contains(t) && !StopWords.Contains(t))
                .Select(t => new DocumentTerm(t, counts[t]))
                .OrderByDescending(t => t.Count)
        ];
    }

    /// <summary>
    /// Terms the document treats two ways at once.
    ///
    /// This needs no classification and no list, which is what makes it scale.
    /// For each derived term, count the accepted lines that kept it verbatim
    /// against those that translated it away. A term always kept is fine; a term
    /// always translated is fine. A term sometimes one and sometimes the other is
    /// the defect actually observed -- "provision" appeared eight different ways
    /// in one document, twice left in English.
    ///
    /// Nobody has to know the right answer for this to work: it reports the split
    /// and the majority, and the operator settles it once with a glossary row.
    /// </summary>
    public static IReadOnlyList<TermInconsistency> Inconsistencies(
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<string> targetLines,
        IReadOnlyList<DocumentTerm> terms,
        int minOccurrences = 3)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        ArgumentNullException.ThrowIfNull(targetLines);
        ArgumentNullException.ThrowIfNull(terms);

        var found = new List<TermInconsistency>();

        foreach (var term in terms)
        {
            var rx = new Regex(@"\b" + Regex.Escape(term.Term) + @"\b", RegexOptions.IgnoreCase);

            var kept = 0;
            var translated = 0;
            var keptAt = new List<int>();

            for (var i = 0; i < targetLines.Count && i < sourceLines.Count; i++)
            {
                // Not translated at all, so it says nothing about the term.
                if (string.Equals(sourceLines[i], targetLines[i], StringComparison.Ordinal))
                {
                    continue;
                }

                // Only prose occurrences count: inside code the term is protected
                // and is SUPPOSED to survive verbatim, which would look like
                // "kept".
                var source = InlineCodeSpan.Replace(sourceLines[i], " ");

                if (!rx.IsMatch(source))
                {
                    continue;
                }

                if (rx.IsMatch(InlineCodeSpan.Replace(targetLines[i], " ")))
                {
                    kept++;
                    keptAt.Add(i + 1);
                }
                else
                {
                    translated++;
                }
            }

            if (kept + translated < minOccurrences || kept == 0 || translated == 0)
            {
                continue;
            }

            found.Add(new TermInconsistency(
                term.Term,
                kept,
                translated,
                translated >= kept ? "translated" : "kept",
                keptAt));
        }

        // Most evenly split first: a term that is half one and half the other is
        // the one nobody has decided about.
        return [.. found.OrderByDescending(f => Math.Min(f.Kept, f.Translated))];
    }
}
