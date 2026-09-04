namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class EmptyOutputCheck : CoverageCheck
{
    public override string CheckId => Checks.CheckId.Coverage.EmptyOutput;

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        foreach (var pair in alignment.Pairs)
        {
            if (pair.TargetRange is null || !pair.Translatable)
            {
                continue;
            }

            var visible = CoverageText.Visible(context.Target.Text, pair.TargetRange, pair.TargetHidden);

            if (!CoverageText.IsBlank(visible))
            {
                continue;
            }

            findings.Add(Finding(
                pair.TargetRange,
                pair.SourceRange,
                CheckGranularity.Block,
                CheckSeverity.Defect,
                100,
                CauseOf(pair),
                $"unit '{pair.Identity}' came back empty for source '{Excerpt(pair.SourceRaw)}'"));
        }
    }
}
