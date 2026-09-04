namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class SourceTokenSurvivalCheck : CoverageCheck
{
    public const int WordLevelSignals = 2;

    private readonly SourceLanguageEvidence? _evidence;

    public SourceTokenSurvivalCheck()
    {
    }

    public SourceTokenSurvivalCheck(SourceLanguageEvidence evidence)
    {
        _evidence = evidence;
    }

    public override string CheckId => Checks.CheckId.Coverage.SourceTokenSurvival;

    private SourceLanguageEvidence Evidence => _evidence ?? CoverageServices.Default;

    private static bool Bracketed(string text, CheckRange range) =>
        range.Offset > 0
        && range.End < text.Length
        && ((text[range.Offset - 1] == '<' && text[range.End] == '>') || text[range.Offset - 1] == '-' || text[range.Offset - 1] == '{');

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        var source = context.Settings.SourceLanguage;
        var target = context.Settings.TargetLanguage;

        if (string.IsNullOrWhiteSpace(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var pair in alignment.Pairs)
        {
            if (pair.TargetRange is null || !pair.Translatable)
            {
                continue;
            }

            var sourceKeys = pair.SourceTokens
                .Where(t => t.Kind == CoverageTokenKind.Word && !pair.SourceHidden.Any(h => h.Contains(t.Range)))
                .Select(t => t.Key)
                .ToHashSet(StringComparer.Ordinal);

            var aligned = pair.Anchors
                .Where(a => a.Identical)
                .Select(a => a.Target.Range)
                .ToHashSet();

            foreach (var token in pair.TargetTokens)
            {
                if (token.Kind != CoverageTokenKind.Word || token.Text.Length < 2 || !sourceKeys.Contains(token.Key))
                {
                    continue;
                }

                if (pair.TargetHidden.Any(h => h.Contains(token.Range)) || ExemptionFilter.Shields(context, token.Range))
                {
                    continue;
                }

                if (LanguageSeeds.Knows(target, token.Key) || context.Target.Invariants.Any(i => i.Range.Contains(token.Range)) || Bracketed(context.Target.Text, token.Range))
                {
                    continue;
                }

                var signals = Evidence.Evaluate(token.Text, source, target);

                if (signals.Count == 0)
                {
                    continue;
                }

                var atAlignedPosition = pair.Ceiling == CheckGranularity.Word && aligned.Contains(token.Range);

                if (atAlignedPosition && signals.Count >= WordLevelSignals)
                {
                    findings.Add(Finding(
                        token.Range,
                        SourceRangeOf(pair, token),
                        CheckGranularity.Word,
                        CheckSeverity.Defect,
                        Math.Min(pair.Confidence, 75 + 10 * signals.Count),
                        CauseOf(pair),
                        $"source token '{token.Text}' survived at its aligned position ({signals.Describe()})"));
                    continue;
                }

                var granularity = AtLeast(pair.Ceiling, CheckGranularity.Sentence);
                var severity = signals.Count >= WordLevelSignals ? CheckSeverity.Defect : CheckSeverity.Score;
                var confidence = signals.Count >= WordLevelSignals ? Math.Min(pair.Confidence, 60) : Math.Min(pair.Confidence, 35);

                findings.Add(Finding(
                    pair.TargetRange,
                    pair.SourceRange,
                    granularity,
                    severity,
                    confidence,
                    CauseOf(pair),
                    $"source token '{token.Text}' present in unit '{pair.Identity}' ({signals.Describe()}, {signals.Count} signal(s))"));
            }
        }
    }

    private static CheckRange? SourceRangeOf(UnitPair pair, CoverageToken target) =>
        pair.Anchors.FirstOrDefault(a => a.Identical && a.Target.Range == target.Range)?.Source.Range;
}
