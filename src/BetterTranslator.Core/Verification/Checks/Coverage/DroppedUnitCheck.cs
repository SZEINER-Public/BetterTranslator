namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class DroppedUnitCheck : CoverageCheck
{
    public override string CheckId => Checks.CheckId.Coverage.DroppedUnit;

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        for (var i = 0; i < alignment.Pairs.Count; i++)
        {
            var pair = alignment.Pairs[i];

            if (pair.TargetRange is not null || !pair.Translatable)
            {
                continue;
            }

            var gap = GapAround(alignment, i, context.Target);
            var confidence = pair.FromTrace ? 100 : 90;

            findings.Add(Finding(
                gap,
                pair.SourceRange,
                CheckGranularity.Block,
                CheckSeverity.Defect,
                confidence,
                CauseOf(pair),
                $"unit '{pair.Identity}' has no target unit for source '{Excerpt(pair.SourceRaw)}'"));
        }
    }
}
