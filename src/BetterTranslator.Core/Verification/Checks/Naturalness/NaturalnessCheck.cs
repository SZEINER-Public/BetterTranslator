using System.Globalization;
using BetterTranslator.Core.Verification.Checks.Coverage;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed record NaturalnessHit(CheckRange TargetRange, CheckRange? SourceRange, string Evidence, int Confidence = 70);

public sealed record NaturalnessSentence(UnitPair Unit, CheckRange Range, IReadOnlyList<CoverageToken> Words);

public abstract class NaturalnessCheck : ICheck
{
    public const int DecidableConfidence = 80;

    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Naturalness.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = NaturalnessPorts.For(context);

        if (services.SkipReason(context.Settings) is not null)
        {
            return [];
        }

        var pack = services.PackFor(context.Settings.TargetLanguage)!;
        var rule = pack.Rule(CheckId);

        if (rule is null)
        {
            return [];
        }

        var decidable = rule.Decidable && (!rule.NeedsTags || services.AnalyzerAvailableFor(context.Settings.TargetLanguage));
        var findings = new List<CheckFinding>();
        var store = NaturalnessEvidenceStore.For(context);

        foreach (var hit in Examine(context, services, pack, Sentences(context)))
        {
            if (context.Exemptions.IsExempt(hit.TargetRange) || context.Exemptions.Spans.Any(s => s.Range.Overlaps(hit.TargetRange)))
            {
                continue;
            }

            if (decidable)
            {
                findings.Add(new CheckFinding(
                    CheckId,
                    hit.TargetRange,
                    hit.SourceRange,
                    Granularity,
                    rule.CheckSeverity,
                    Math.Min(hit.Confidence, DecidableConfidence),
                    CheckCause.ModelOutput,
                    hit.Evidence,
                    rule.CheckSeverity == CheckSeverity.Advisory ? CheckAction.Mark : CheckAction.Rewrite));
                continue;
            }

            store.Add(new NaturalnessEvidence(CheckId, UnitOf(context, hit.TargetRange), hit.TargetRange, hit.SourceRange, hit.Evidence));
        }

        return
        [
            .. findings
                .OrderBy(f => f.TargetRange.Offset)
                .ThenBy(f => f.TargetRange.Length)
                .ThenBy(f => f.Evidence, StringComparer.Ordinal),
        ];
    }

    protected virtual CheckGranularity Granularity => CheckGranularity.Sentence;

    protected abstract IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences);

    public static IReadOnlyList<NaturalnessSentence> Sentences(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sentences = new List<NaturalnessSentence>();

        foreach (var pair in CoverageAlignment.Of(context).Pairs.Where(p => p.HasTarget && p.Translatable))
        {
            if (CoverageDefects.Untranslated(context, pair.TargetRange!))
            {
                continue;
            }

            foreach (var range in NaturalnessSentences.Split(context.Target.Text, pair.TargetRange!))
            {
                if (context.Exemptions.Spans.Any(s => s.Range.Overlaps(range) && s.Range.Contains(range)))
                {
                    continue;
                }

                var words = pair.TargetTokens.Where(t => t.Kind == CoverageTokenKind.Word && range.Contains(t.Range) && !pair.TargetHidden.Any(h => h.Overlaps(t.Range))).ToList();
                sentences.Add(new NaturalnessSentence(pair, range, words));
            }
        }

        return sentences;
    }

    protected static string Fill(NaturalnessRule rule, params (string Key, object Value)[] values)
    {
        var text = rule.Evidence;

        foreach (var (key, value) in values)
        {
            text = text.Replace("{" + key + "}", Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return text;
    }

    protected static IReadOnlyList<IReadOnlyList<CoverageToken>> Clauses(NaturalnessSentence sentence, NaturalnessPack pack, string text)
    {
        var clauses = new List<IReadOnlyList<CoverageToken>>();
        var current = new List<CoverageToken>();
        var breakers = new HashSet<string>(pack.ClauseBreakers, StringComparer.OrdinalIgnoreCase);

        foreach (var token in sentence.Unit.TargetTokens.Where(t => sentence.Range.Contains(t.Range)))
        {
            if (token.Kind == CoverageTokenKind.Punctuation && breakers.Contains(token.Text))
            {
                Flush(clauses, current);
                current = [];
                continue;
            }

            if (token.Kind == CoverageTokenKind.Word && sentence.Words.Contains(token))
            {
                current.Add(token);
            }
        }

        Flush(clauses, current);
        return clauses;
    }

    private static void Flush(List<IReadOnlyList<CoverageToken>> clauses, List<CoverageToken> current)
    {
        if (current.Count > 0)
        {
            clauses.Add(current);
        }
    }

    private static string UnitOf(CheckContext context, CheckRange range) =>
        CoverageAlignment.Of(context).Pairs.FirstOrDefault(p => p.TargetRange is not null && p.TargetRange.Overlaps(range))?.Identity ?? "document";

    protected static string Pair(CheckContext context) => NaturalnessProfile.Key(context.Settings.SourceLanguage, context.Settings.TargetLanguage);

    protected static NaturalnessPairProfile? ProfileFor(CheckContext context, NaturalnessServices services, UnitPair pair)
    {
        var unit = Ratio.RatioMeasures.SentenceCount(pair.SourceRaw) <= 1 ? NaturalnessProfile.UnitSentence : NaturalnessProfile.UnitBlock;

        return services.Profile.For(context.Settings.SourceLanguage, context.Settings.TargetLanguage, unit)
            ?? services.Profile.For(context.Settings.SourceLanguage, context.Settings.TargetLanguage, NaturalnessProfile.UnitSentence);
    }
}
