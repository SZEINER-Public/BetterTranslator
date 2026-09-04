namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class PlaceholderCensusCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.PlaceholderCensus;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        foreach (var segment in context.Alignment)
        {
            if (segment.Masks.Count == 0)
            {
                continue;
            }

            var missing = segment.Masks.Where(m => m.ReturnedCount == 0).ToList();
            var duplicated = segment.Masks.Where(m => m.ReturnedCount > 1).ToList();
            var unrestored = segment.TargetRange is null
                ? []
                : segment.Masks.Where(m => m.ReturnedCount >= 1 && m.TargetRange is null).ToList();
            var drifted = segment.Masks.Where(m => m.ReturnedCount == 1 && !m.ReturnedIntact && m.TargetRange is not null).ToList();
            var span = segment.TargetRange ?? context.Target.Whole;

            if (missing.Count > 0 || duplicated.Count > 0 || unrestored.Count > 0)
            {
                var parts = new List<string>
                {
                    $"placeholders emitted {segment.Masks.Count}, returned once {segment.Masks.Count(m => m.ReturnedCount == 1)}",
                };

                if (missing.Count > 0)
                {
                    parts.Add("missing " + string.Join(",", missing.Select(m => m.Sentinel)));
                }

                if (duplicated.Count > 0)
                {
                    parts.Add("duplicated " + string.Join(",", duplicated.Select(m => m.Sentinel)));
                }

                if (unrestored.Count > 0)
                {
                    parts.Add("not restored " + string.Join(",", unrestored.Select(m => m.Sentinel)));
                }

                findings.Add(Finding(
                    span,
                    segment.SourceRange,
                    CheckGranularity.Block,
                    CheckSeverity.Defect,
                    100,
                    missing.Count > 0 || duplicated.Count > 0 ? CheckCause.ModelOutput : CheckCause.Restore,
                    string.Join("; ", parts),
                    CheckAction.Repair));
            }
            else if (drifted.Count > 0)
            {
                findings.Add(Finding(
                    span,
                    segment.SourceRange,
                    CheckGranularity.Block,
                    CheckSeverity.Advisory,
                    80,
                    CheckCause.ModelOutput,
                    "placeholders returned in a drifted form and normalized: " + string.Join(",", drifted.Select(m => m.Sentinel))));
            }
        }

        var known = context.Source.Placeholders
            .GroupBy(p => p.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var residue in context.Target.Placeholders)
        {
            if (known.TryGetValue(residue.Text, out var count) && count > 0)
            {
                known[residue.Text] = count - 1;
                continue;
            }

            findings.Add(Finding(
                residue.Range,
                null,
                CheckGranularity.Word,
                CheckSeverity.Defect,
                95,
                CheckCause.Restore,
                $"placeholder text '{residue.Text}' survives in the output",
                CheckAction.Repair));
        }
    }
}
