using System.Runtime.CompilerServices;
using BetterTranslator.Core.Verification.Checks.Coverage;

namespace BetterTranslator.Core.Verification.Checks.Terminology;

public enum RenderingKind
{
    Accepted,
    Rejected,
    Inferred,
    Missing,
}

public sealed record TermOccurrence(
    string SourceTerm,
    string UnitIdentity,
    int UnitIndex,
    CheckRange SourceRange,
    CheckRange TargetRange,
    CheckRange UnitTargetRange,
    string Rendering,
    string Lemma,
    RenderingKind Kind)
{
    public bool Rendered => Kind != RenderingKind.Missing;
}

public sealed record TermEntry(string SourceTerm, TerminologyEntry? Glossary, IReadOnlyList<TermOccurrence> Occurrences)
{
    public IReadOnlyList<string> DistinctLemmas =>
        [.. Occurrences.Where(o => o.Rendered).Select(o => o.Lemma).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public string MajorityLemma =>
        Occurrences.Where(o => o.Rendered)
            .GroupBy(o => o.Lemma, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

    public string MajorityRendering =>
        Occurrences.Where(o => o.Rendered && string.Equals(o.Lemma, MajorityLemma, StringComparison.Ordinal))
            .Select(o => o.Rendering)
            .OrderBy(r => r, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;

    public int MajorityCount => Occurrences.Count(o => o.Rendered && string.Equals(o.Lemma, MajorityLemma, StringComparison.Ordinal));
}

public sealed class TermIndex
{
    private const int InferredMinimumLength = 4;

    private const int InferredMinimumUnits = 2;

    private static readonly ConditionalWeakTable<CheckContext, TermIndex> Cache = new();

    private readonly SortedDictionary<string, TermEntry> _terms = new(StringComparer.Ordinal);

    private TermIndex(IEnumerable<TermEntry> entries)
    {
        foreach (var entry in entries)
        {
            _terms[entry.SourceTerm] = entry;
        }
    }

    public IReadOnlyList<TermEntry> Terms => [.. _terms.Values];

    public TermEntry? Find(string sourceTerm)
    {
        ArgumentNullException.ThrowIfNull(sourceTerm);

        return _terms.TryGetValue(sourceTerm.ToLowerInvariant(), out var entry) ? entry : null;
    }

    public static TermIndex For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Cache.GetValue(context, c => Build(c, TerminologyPorts.For(c)));
    }

    public static TermIndex Build(CheckContext context, TerminologyServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        var units = CoverageAlignment.Of(context).Pairs
            .Where(pair => pair.HasTarget && pair.Translatable)
            .Select((pair, index) => new Unit(index, pair, Visible(pair.SourceTokens, pair.SourceHidden), Words(pair.TargetTokens)))
            .ToList();

        var exemptTexts = new HashSet<string>(context.Exemptions.Spans.Select(s => s.Text.ToLowerInvariant()), StringComparer.Ordinal);
        var lemmatizer = services.Lemmatizer;
        var targetLemmas = units.Select(u => u.TargetWords.Select(t => Lemmas.Of(lemmatizer, t.Text)).ToList()).ToList();

        var entries = new List<TermEntry>();

        foreach (var term in Candidates(services.Glossary, units, exemptTexts))
        {
            var words = Lemmas.Words(term.Source).Select(w => w.ToLowerInvariant()).ToList();
            IReadOnlyList<string> accepted = term.Glossary is null ? [] : Lemmas.Sequence(lemmatizer, term.Glossary.Accepted);
            IReadOnlyList<IReadOnlyList<string>> rejected = term.Glossary is null ? [] : [.. term.Glossary.Rejected.Select(r => Lemmas.Sequence(lemmatizer, r))];
            var background = Background(units, targetLemmas, words);
            var occurrences = new List<TermOccurrence>();

            foreach (var unit in units)
            {
                foreach (var hit in Matches(unit.SourceWords, words))
                {
                    var sourceRange = Span(unit.SourceWords, hit, words.Count);
                    occurrences.Add(Render(term, unit, targetLemmas[unit.Index], sourceRange, hit, accepted, rejected, background));
                }
            }

            if (occurrences.Count > 0)
            {
                entries.Add(new TermEntry(term.Source.ToLowerInvariant(), term.Glossary, occurrences));
            }
        }

        return new TermIndex(entries);
    }

    private static TermOccurrence Render(
        Candidate term,
        Unit unit,
        IReadOnlyList<string> lemmas,
        CheckRange sourceRange,
        int sourceHit,
        IReadOnlyList<string> accepted,
        IReadOnlyList<IReadOnlyList<string>> rejected,
        ISet<string> background)
    {
        var key = term.Source.ToLowerInvariant();
        var words = unit.TargetWords;

        if (accepted.Count > 0)
        {
            foreach (var at in Matches(lemmas, accepted))
            {
                return new TermOccurrence(key, unit.Pair.Identity, unit.Index, sourceRange, Span(words, at, accepted.Count), unit.Pair.TargetRange!, Surface(words, at, accepted.Count), string.Join(' ', accepted), RenderingKind.Accepted);
            }
        }

        foreach (var sequence in rejected.Where(s => s.Count > 0))
        {
            foreach (var at in Matches(lemmas, sequence))
            {
                return new TermOccurrence(key, unit.Pair.Identity, unit.Index, sourceRange, Span(words, at, sequence.Count), unit.Pair.TargetRange!, Surface(words, at, sequence.Count), string.Join(' ', sequence), RenderingKind.Rejected);
            }
        }

        var inferred = Infer(unit, lemmas, sourceHit, background);

        return inferred is { } index
            ? new TermOccurrence(key, unit.Pair.Identity, unit.Index, sourceRange, words[index].Range, unit.Pair.TargetRange!, words[index].Text, lemmas[index], RenderingKind.Inferred)
            : new TermOccurrence(key, unit.Pair.Identity, unit.Index, sourceRange, unit.Pair.TargetRange!, unit.Pair.TargetRange!, string.Empty, string.Empty, RenderingKind.Missing);
    }

    private static int? Infer(Unit unit, IReadOnlyList<string> lemmas, int sourceHit, ISet<string> background)
    {
        if (unit.TargetWords.Count == 0 || unit.SourceWords.Count == 0)
        {
            return null;
        }

        var sourcePosition = sourceHit / (double)unit.SourceWords.Count;
        int? best = null;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < unit.TargetWords.Count; i++)
        {
            var word = unit.TargetWords[i];

            if (word.Text.Length < 3 || background.Contains(lemmas[i]) || !word.Text.All(char.IsLetter) || unit.Pair.TargetHidden.Any(h => h.Overlaps(word.Range)))
            {
                continue;
            }

            var distance = Math.Abs(i / (double)unit.TargetWords.Count - sourcePosition);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static HashSet<string> Background(IReadOnlyList<Unit> units, IReadOnlyList<List<string>> targetLemmas, IReadOnlyList<string> words)
    {
        var background = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            if (Matches(unit.SourceWords, words).Any())
            {
                continue;
            }

            background.UnionWith(targetLemmas[unit.Index]);
        }

        return background;
    }

    private static IEnumerable<Candidate> Candidates(TerminologyGlossary glossary, IReadOnlyList<Unit> units, ISet<string> exemptTexts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in glossary.Entries)
        {
            var key = entry.Source.ToLowerInvariant();
            seen.Add(key);

            if (entry.DoNotTranslate || exemptTexts.Contains(key))
            {
                continue;
            }

            yield return new Candidate(entry.Source, entry);
        }

        var perWord = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            foreach (var token in unit.SourceWords)
            {
                var key = token.Key;

                if (key.Length < InferredMinimumLength || !key.All(char.IsLetter) || seen.Contains(key) || exemptTexts.Contains(key))
                {
                    continue;
                }

                if (!perWord.TryGetValue(key, out var set))
                {
                    set = [];
                    perWord[key] = set;
                }

                set.Add(unit.Index);
            }
        }

        foreach (var key in perWord.Where(p => p.Value.Count >= InferredMinimumUnits).Select(p => p.Key).Order(StringComparer.Ordinal))
        {
            yield return new Candidate(key, null);
        }
    }

    private static IEnumerable<int> Matches(IReadOnlyList<CoverageToken> tokens, IReadOnlyList<string> words) =>
        Matches(tokens.Select(t => t.Key).ToList(), words);

    private static IEnumerable<int> Matches(IReadOnlyList<string> keys, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0)
        {
            yield break;
        }

        for (var i = 0; i + sequence.Count <= keys.Count; i++)
        {
            var match = true;

            for (var j = 0; j < sequence.Count; j++)
            {
                if (!string.Equals(keys[i + j], sequence[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                yield return i;
            }
        }
    }

    private static CheckRange Span(IReadOnlyList<CoverageToken> tokens, int start, int count)
    {
        var first = tokens[start].Range;
        var last = tokens[start + count - 1].Range;

        return new CheckRange(first.UnitPath, first.Offset, last.End - first.Offset);
    }

    private static string Surface(IReadOnlyList<CoverageToken> tokens, int start, int count) =>
        string.Join(' ', tokens.Skip(start).Take(count).Select(t => t.Text));

    private static List<CoverageToken> Visible(IReadOnlyList<CoverageToken> tokens, IReadOnlyList<CheckRange> hidden) =>
        [.. tokens.Where(t => t.Kind == CoverageTokenKind.Word && !hidden.Any(h => h.Overlaps(t.Range)))];

    private static List<CoverageToken> Words(IReadOnlyList<CoverageToken> tokens) =>
        [.. tokens.Where(t => t.Kind == CoverageTokenKind.Word)];

    private sealed record Unit(int Index, UnitPair Pair, List<CoverageToken> SourceWords, List<CoverageToken> TargetWords);

    private sealed record Candidate(string Source, TerminologyEntry? Glossary);
}
