namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class ExemptionFilterCheck : CoverageCheck
{
    public override string CheckId => Checks.CheckId.Coverage.ExemptionFilter;

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        foreach (var span in context.Exemptions.Spans.OrderBy(s => s.Range.Offset).ThenBy(s => s.Range.Length))
        {
            if (ExemptionFilter.Honored(context.Target, span))
            {
                continue;
            }

            var inside = span.Range.Offset >= 0 && span.Range.End <= context.Target.Length;
            var target = inside
                ? span.Range
                : new CheckRange(span.Range.UnitPath, Math.Clamp(span.Range.Offset, 0, context.Target.Length), 0);
            var pair = alignment.Pairs.FirstOrDefault(p => p.TargetRange is not null && p.TargetRange.Overlaps(target));

            findings.Add(Finding(
                target,
                pair?.SourceRange,
                CheckGranularity.Word,
                CheckSeverity.Advisory,
                100,
                pair is null ? CheckCause.AdapterRebuild : CauseOf(pair),
                inside
                    ? $"exempt span ({span.Reason}) '{span.Text}' does not match the target text at its range and was not subtracted"
                    : $"exempt span ({span.Reason}) '{span.Text}' lies outside the target document",
                CheckAction.ScoreOnly));
        }
    }
}
