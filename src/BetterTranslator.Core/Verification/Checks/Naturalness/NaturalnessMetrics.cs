using System.Runtime.CompilerServices;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Core.Verification.Checks.Ratio;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public static class NaturalnessSentences
{
    public static IReadOnlyList<CheckRange> Split(string text, CheckRange range)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(range);

        var sentences = new List<CheckRange>();
        var end = Math.Min(text.Length, range.End);
        var start = Math.Max(0, range.Offset);
        var cursor = start;

        for (var i = start; i < end; i++)
        {
            var terminal = text[i] is '.' or '!' or '?' or '\n';

            if (!terminal)
            {
                continue;
            }

            while (i + 1 < end && text[i + 1] is '.' or '!' or '?' or '"' or '“' or '”' or '»' or ')')
            {
                i++;
            }

            Add(sentences, text, range.UnitPath, cursor, i + 1);
            cursor = i + 1;
        }

        Add(sentences, text, range.UnitPath, cursor, end);
        return sentences;
    }

    private static void Add(List<CheckRange> sentences, string text, string unitPath, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(text[to - 1]))
        {
            to--;
        }

        if (to > from && text.AsSpan(from, to - from).ToString().Any(char.IsLetter))
        {
            sentences.Add(new CheckRange(unitPath, from, to - from));
        }
    }
}

public sealed record CrossingMeasure(int Crossings, int Matched, int SourceWords)
{
    public double Normalized => Matched == 0 ? 0 : Crossings / (double)Matched;
}

public static class AlignmentCrossing
{
    public static CrossingMeasure Compute(UnitPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var source = pair.SourceTokens.Where(t => t.Kind != CoverageTokenKind.Punctuation && !pair.SourceHidden.Any(h => h.Overlaps(t.Range))).ToList();
        var target = pair.TargetTokens.Where(t => t.Kind != CoverageTokenKind.Punctuation && !pair.TargetHidden.Any(h => h.Overlaps(t.Range))).ToList();

        var sourceUnique = source.GroupBy(t => t.Key, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => source.IndexOf(g.First()), StringComparer.Ordinal);
        var targetUnique = target.GroupBy(t => t.Key, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => target.IndexOf(g.First()), StringComparer.Ordinal);

        var matched = sourceUnique
            .Where(p => targetUnique.ContainsKey(p.Key))
            .Select(p => (Source: p.Value, Target: targetUnique[p.Key]))
            .OrderBy(p => p.Source)
            .ToList();

        var crossings = 0;

        for (var i = 0; i < matched.Count; i++)
        {
            for (var j = i + 1; j < matched.Count; j++)
            {
                if (matched[j].Target < matched[i].Target)
                {
                    crossings++;
                }
            }
        }

        return new CrossingMeasure(crossings, matched.Count, source.Count(t => t.Kind == CoverageTokenKind.Word));
    }
}

public static class TagSequences
{
    public static IReadOnlyList<string> TagsOf(ITagger tagger, IEnumerable<CoverageToken> words)
    {
        ArgumentNullException.ThrowIfNull(tagger);
        ArgumentNullException.ThrowIfNull(words);

        return [.. words.Select(w => tagger.Tags(w.Text).FirstOrDefault() ?? tagger.Tags(w.Text.ToLowerInvariant()).FirstOrDefault() ?? "unknown")];
    }

    public static IReadOnlyDictionary<string, int> Bigrams(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i + 1 < tags.Count; i++)
        {
            var key = Coarse(tags[i]) + ">" + Coarse(tags[i + 1]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    public static string Coarse(string tag) => tag.Length >= 2 ? tag[..2] : tag;

    public static double Divergence(IReadOnlyDictionary<string, int> segment, IReadOnlyDictionary<string, int> reference)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(reference);

        var total = segment.Values.Sum();

        if (total == 0 || reference.Count == 0)
        {
            return 0;
        }

        var referenceTotal = Math.Max(1, reference.Values.Sum());
        var unseen = 0.0;

        foreach (var (key, count) in segment)
        {
            var expected = reference.GetValueOrDefault(key) / (double)referenceTotal;
            var observed = count / (double)total;
            unseen += Math.Max(0, observed - expected);
        }

        return Math.Round(unseen, 3);
    }
}

public sealed record RegisterProfile(string Formality, string Person, string Instruction, string Tense)
{
    public const string Unknown = "unknown";

    public static RegisterProfile Empty { get; } = new(Unknown, Unknown, Unknown, Unknown);

    public IReadOnlyList<string> Deviations(RegisterProfile document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var deviations = new List<string>();

        if (Differs(Formality, document.Formality))
        {
            deviations.Add("formality " + Formality + " against " + document.Formality);
        }

        if (Differs(Instruction, document.Instruction))
        {
            deviations.Add("instruction form " + Instruction + " against " + document.Instruction);
        }

        return deviations;
    }

    private static bool Differs(string segment, string document) =>
        segment != Unknown && document != Unknown && !string.Equals(segment, document, StringComparison.Ordinal);
}

public static class RegisterProfiles
{
    private static readonly ConditionalWeakTable<CheckContext, RegisterProfile> Documents = new();

    public static RegisterProfile Of(IReadOnlyList<string> tags, NaturalnessPack pack)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(pack);

        var verb = pack.Tag("verb");
        var verbs = tags.Where(t => verb.Length > 0 && t.StartsWith(verb, StringComparison.Ordinal)).ToList();

        return new RegisterProfile(
            Majority(verbs, [("formal", pack.Tag("secondPersonPlural")), ("informal", pack.Tag("secondPersonSingular"))]),
            Majority(verbs, [("first", pack.Tag("person") + "1"), ("second", pack.Tag("person") + "2"), ("third", pack.Tag("person") + "3")]),
            Majority(verbs, [("imperative", pack.Tag("imperative")), ("infinitive", pack.Tag("infinitive"))]),
            TenseOf(verbs, pack));
    }

    public static RegisterProfile Document(CheckContext context, NaturalnessPack pack, ITagger tagger)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Documents.GetValue(context, c =>
        {
            var tags = new List<string>();

            foreach (var pair in CoverageAlignment.Of(c).Pairs.Where(p => p.HasTarget && p.Translatable))
            {
                tags.AddRange(TagSequences.TagsOf(tagger, pair.TargetTokens.Where(t => t.Kind == CoverageTokenKind.Word)));
            }

            return Of(tags, pack);
        });
    }

    private static string Majority(IReadOnlyList<string> verbs, IReadOnlyList<(string Label, string Marker)> markers)
    {
        var best = RegisterProfile.Unknown;
        var bestCount = 0;

        foreach (var (label, marker) in markers)
        {
            if (marker.Length == 0)
            {
                continue;
            }

            var count = verbs.Count(t => t.Contains(marker, StringComparison.Ordinal));

            if (count > bestCount)
            {
                bestCount = count;
                best = label;
            }
        }

        return best;
    }

    private static string TenseOf(IReadOnlyList<string> verbs, NaturalnessPack pack)
    {
        var key = pack.Tag("tense");

        if (key.Length == 0)
        {
            return RegisterProfile.Unknown;
        }

        var letters = verbs
            .Select(t => t.IndexOf(key, StringComparison.Ordinal) is var at && at >= 0 && at + key.Length < t.Length ? t[at + key.Length].ToString() : null)
            .Where(l => l is not null)
            .GroupBy(l => l!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        return letters?.Key ?? RegisterProfile.Unknown;
    }
}

public sealed record SentenceFit(int Source, int Target)
{
    public int Excess => Target - Source;
}

public static class SentenceBoundaryFit
{
    public static SentenceFit Compute(UnitPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        return new SentenceFit(RatioMeasures.SentenceCount(pair.SourceRaw), RatioMeasures.SentenceCount(pair.TargetRaw));
    }
}

public static class CoverageDefects
{
    private static readonly ConditionalWeakTable<CheckContext, IReadOnlyList<CheckFinding>> Cache = new();

    public static IReadOnlyList<CheckFinding> Of(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Cache.GetValue(context, c =>
        {
            var findings = new List<CheckFinding>();

            foreach (var check in new CoverageCheck[] { new CopyThroughCheck(), new EmptyOutputCheck(), new DroppedUnitCheck(), new SourceTokenSurvivalCheck() })
            {
                findings.AddRange(check.Run(c).Where(f => f.Severity == CheckSeverity.Defect));
            }

            return findings;
        });
    }

    public static bool Untranslated(CheckContext context, CheckRange range) =>
        Of(context).Any(f => f.TargetRange.Overlaps(range) || (f.TargetRange.Length == 0 && f.TargetRange.Offset >= range.Offset && f.TargetRange.Offset <= range.End));
}
