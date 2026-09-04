namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class PlaceholderDefectCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.PlaceholderDefect;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        foreach (var segment in context.Alignment)
        {
            var span = segment.TargetRange ?? context.Target.Whole;

            foreach (var mask in segment.Masks)
            {
                if (mask.ReturnedCount == 0)
                {
                    findings.Add(Finding(
                        span,
                        mask.SourceRange,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        100,
                        CheckCause.ModelOutput,
                        $"placeholder {mask.Sentinel} for '{mask.Original}' absent from the answer",
                        CheckAction.Repair));
                    continue;
                }

                if (mask.ReturnedCount > 1)
                {
                    findings.Add(Finding(
                        span,
                        mask.SourceRange,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        100,
                        CheckCause.ModelOutput,
                        $"placeholder {mask.Sentinel} for '{mask.Original}' duplicated {mask.ReturnedCount} times",
                        CheckAction.Repair));
                    continue;
                }

                if (mask.TargetRange is null)
                {
                    if (segment.TargetRange is null)
                    {
                        continue;
                    }

                    findings.Add(Finding(
                        span,
                        mask.SourceRange,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        95,
                        CheckCause.Restore,
                        $"placeholder {mask.Sentinel} returned but '{mask.Original}' was not restored",
                        CheckAction.Repair));
                    continue;
                }

                if (!mask.ReturnedIntact)
                {
                    findings.Add(Finding(
                        span,
                        mask.SourceRange,
                        CheckGranularity.Block,
                        CheckSeverity.Advisory,
                        70,
                        CheckCause.ModelOutput,
                        $"placeholder {mask.Sentinel} for '{mask.Original}' returned altered and was normalized before restore"));
                }
            }
        }
    }
}
