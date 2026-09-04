namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class CopyThroughCheck : CoverageCheck
{
    public override string CheckId => Checks.CheckId.Coverage.CopyThrough;

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        foreach (var pair in alignment.Pairs)
        {
            if (pair.TargetRange is null || !pair.Translatable || pair.TargetNormalized.Length == 0)
            {
                continue;
            }

            if (!string.Equals(pair.SourceNormalized, pair.TargetNormalized, StringComparison.Ordinal))
            {
                continue;
            }

            var words = pair.SourceNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var severity = words >= 2 ? CheckSeverity.Defect : CheckSeverity.Score;
            var confidence = words switch
            {
                >= 3 => 100,
                2 => 80,
                _ => 50,
            };

            findings.Add(Finding(
                pair.TargetRange,
                pair.SourceRange,
                CheckGranularity.Block,
                severity,
                confidence,
                CauseOf(pair),
                $"unit '{pair.Identity}' copied through unchanged: '{Excerpt(pair.TargetRaw)}'"));
        }
    }
}
