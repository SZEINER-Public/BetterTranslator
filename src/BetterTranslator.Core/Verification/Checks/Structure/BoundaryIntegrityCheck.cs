namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class BoundaryIntegrityCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.BoundaryIntegrity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        foreach (var segment in context.Alignment)
        {
            foreach (var mask in segment.Masks)
            {
                if (mask.TargetRange is null || !mask.SourceLocated)
                {
                    continue;
                }

                Side(mask, mask.SourceLeft, mask.TargetLeft, "left", findings, mask.ReturnedIntact);
                Side(mask, mask.SourceRight, mask.TargetRight, "right", findings, mask.ReturnedIntact);
            }
        }
    }

    private void Side(MaskRecord mask, DelimiterClass source, DelimiterClass target, string side, List<CheckFinding> findings, bool intact)
    {
        if (JoinsWord(source) == JoinsWord(target))
        {
            return;
        }

        var range = mask.TargetRange!;
        var marked = side == "left" && range.Offset > 0
            ? new CheckRange(range.UnitPath, range.Offset - 1, range.Length + 1)
            : new CheckRange(range.UnitPath, range.Offset, range.Length + 1);

        var fused = JoinsWord(target);

        findings.Add(Finding(
            marked,
            mask.SourceRange,
            CheckGranularity.Word,
            CheckSeverity.Defect,
            fused ? 95 : 60,
            intact ? CheckCause.ModelOutput : CheckCause.Restore,
            fused
                ? $"restored '{mask.Original}' fused on the {side}: {source} in source, {target} in target"
                : $"restored '{mask.Original}' detached on the {side}: {source} in source, {target} in target",
            fused ? CheckAction.Repair : CheckAction.Mark));
    }

    private static bool JoinsWord(DelimiterClass delimiter) => delimiter is DelimiterClass.Letter or DelimiterClass.Digit;
}
