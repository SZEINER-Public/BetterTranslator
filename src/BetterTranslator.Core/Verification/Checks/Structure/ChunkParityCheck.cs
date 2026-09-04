namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class ChunkParityCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.ChunkParity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        var source = context.Source.Chunks;
        var target = context.Target.Chunks;

        if (source.Count == 0 && target.Count == 0)
        {
            return;
        }

        var edits = SequenceDiff.Diff([.. source.Select(c => c.Identity)], [.. target.Select(c => c.Identity)]);
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
                    var dropped = source[sourceIndex];
                    var added = target[edits[e + 1].TargetIndex];

                    findings.Add(Finding(
                        added.Range,
                        dropped.Range,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        90,
                        CauseAt(context, added.Range, dropped.Range),
                        $"chunk '{dropped.Identity}' became '{added.Identity}'"));

                    previousMatched = edits[e + 1].TargetIndex;
                    e++;
                    break;
                }

                case SequenceDiff.Op.Delete:
                {
                    var dropped = source[sourceIndex];
                    var next = NextMatched(edits, e);
                    var gapStart = previousMatched >= 0 ? target[previousMatched].Range.End : 0;
                    var gapEnd = next >= 0 && next < target.Count ? target[next].Range.Offset : context.Target.Length;
                    var path = next >= 0 && next < target.Count ? target[next].Range.UnitPath : DocumentModel.RootPath;
                    var gap = new CheckRange(path, Math.Min(gapStart, gapEnd), Math.Max(0, gapEnd - gapStart));

                    findings.Add(Finding(
                        gap,
                        dropped.Range,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        95,
                        CauseAt(context, gap, dropped.Range),
                        $"chunk '{dropped.Identity}' dropped: {source.Count} chunks in source, {target.Count} in target"));
                    break;
                }

                case SequenceDiff.Op.Insert:
                {
                    var added = target[targetIndex];

                    findings.Add(Finding(
                        added.Range,
                        null,
                        CheckGranularity.Block,
                        CheckSeverity.Defect,
                        90,
                        CauseAt(context, added.Range, null),
                        $"chunk '{added.Identity}' added: {source.Count} chunks in source, {target.Count} in target"));

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
