namespace BetterTranslator.Core.Verification.Checks.Structure;

public abstract class StructureCheck : ICheck
{
    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Structure.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<CheckFinding>();
        Find(context, findings);

        return
        [
            .. findings
                .Where(finding => !context.Exemptions.IsExempt(finding.TargetRange))
                .OrderBy(finding => finding.TargetRange.Offset)
                .ThenBy(finding => finding.TargetRange.Length)
                .ThenBy(finding => finding.Evidence, StringComparer.Ordinal),
        ];
    }

    protected abstract void Find(CheckContext context, List<CheckFinding> findings);

    protected CheckFinding Finding(
        CheckRange target,
        CheckRange? source,
        CheckGranularity granularity,
        CheckSeverity severity,
        int confidence,
        string cause,
        string evidence,
        CheckAction action = CheckAction.Mark) =>
        new(CheckId, target, source, granularity, severity, confidence, cause, evidence, action);

    protected static string CauseAt(CheckContext context, CheckRange? target, CheckRange? source)
    {
        if (context.Alignment.Count == 0)
        {
            return CheckCause.AdapterRebuild;
        }

        var touched = target is null
            ? []
            : context.Alignment.Where(segment => segment.TargetRange is not null && segment.TargetRange.Overlaps(target)).ToList();

        if (touched.Count == 0 && source is not null)
        {
            touched = context.Alignment.Where(segment => segment.SourceRange.Overlaps(source)).ToList();
        }

        if (touched.Count == 0)
        {
            return CheckCause.AdapterRebuild;
        }

        if (touched.Count > 1)
        {
            return CheckCause.Segmentation;
        }

        return touched[0].Outcome switch
        {
            SegmentOutcome.Translated or SegmentOutcome.Recovered => CheckCause.ModelOutput,
            _ => CheckCause.AdapterRebuild,
        };
    }

    protected static CheckRange SegmentTargetOf(CheckContext context, CheckRange source)
    {
        var segment = context.Alignment.FirstOrDefault(s => s.TargetRange is not null && s.SourceRange.Overlaps(source));
        return segment?.TargetRange ?? context.Target.Whole;
    }

    protected static CheckRange Gap(IReadOnlyList<DocumentNode> targetNodes, int nextMatchedIndex, int previousMatchedIndex, DocumentModel target)
    {
        var start = previousMatchedIndex >= 0 ? targetNodes[previousMatchedIndex].Range.End : 0;
        var end = nextMatchedIndex >= 0 && nextMatchedIndex < targetNodes.Count ? targetNodes[nextMatchedIndex].Range.Offset : target.Length;
        var path = nextMatchedIndex >= 0 && nextMatchedIndex < targetNodes.Count
            ? targetNodes[nextMatchedIndex].Path
            : previousMatchedIndex >= 0 ? targetNodes[previousMatchedIndex].Path : DocumentModel.RootPath;

        return new CheckRange(path, Math.Min(start, end), Math.Max(0, end - start));
    }

    protected void DiffNodes(
        CheckContext context,
        IReadOnlyList<DocumentNode> sourceNodes,
        IReadOnlyList<DocumentNode> targetNodes,
        Func<DocumentNode, string> identity,
        string label,
        List<CheckFinding> findings)
    {
        var edits = SequenceDiff.Diff([.. sourceNodes.Select(identity)], [.. targetNodes.Select(identity)]);
        var previousMatched = -1;

        for (var e = 0; e < edits.Count; e++)
        {
            var (op, sourceIndex, targetIndex) = edits[e];

            switch (op)
            {
                case SequenceDiff.Op.Match:
                    previousMatched = targetIndex;
                    break;

                case SequenceDiff.Op.Delete when e + 1 < edits.Count && edits[e + 1].Op == SequenceDiff.Op.Insert:
                {
                    var deleted = sourceNodes[sourceIndex];
                    var inserted = targetNodes[edits[e + 1].TargetIndex];

                    findings.Add(Finding(
                        inserted.Range,
                        deleted.Range,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        100,
                        CauseAt(context, inserted.Range, deleted.Range),
                        $"{label} '{identity(deleted)}' became '{identity(inserted)}'"));

                    previousMatched = edits[e + 1].TargetIndex;
                    e++;
                    break;
                }

                case SequenceDiff.Op.Delete:
                {
                    var deleted = sourceNodes[sourceIndex];
                    var nextMatched = NextMatched(edits, e);
                    var gap = Gap(targetNodes, nextMatched, previousMatched, context.Target);

                    findings.Add(Finding(
                        gap,
                        deleted.Range,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        100,
                        CauseAt(context, gap, deleted.Range),
                        $"{label} '{identity(deleted)}' missing from target"));
                    break;
                }

                case SequenceDiff.Op.Insert:
                {
                    var inserted = targetNodes[targetIndex];

                    findings.Add(Finding(
                        inserted.Range,
                        null,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        100,
                        CauseAt(context, inserted.Range, null),
                        $"{label} '{identity(inserted)}' added in target"));

                    previousMatched = targetIndex;
                    break;
                }
            }
        }
    }

    private static int NextMatched(IReadOnlyList<(SequenceDiff.Op Op, int SourceIndex, int TargetIndex)> edits, int from)
    {
        for (var i = from + 1; i < edits.Count; i++)
        {
            if (edits[i].Op != SequenceDiff.Op.Delete)
            {
                return edits[i].TargetIndex;
            }
        }

        return -1;
    }
}
