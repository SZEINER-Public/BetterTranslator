namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class SubtitleParityCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.SubtitleParity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        var source = context.Source.OfKind(DocumentNodeKind.Cue).ToList();

        if (source.Count == 0)
        {
            return;
        }

        var target = context.Target.OfKind(DocumentNodeKind.Cue).ToList();

        if (source.Count != target.Count)
        {
            findings.Add(Finding(
                context.Target.Whole,
                context.Source.Whole,
                CheckGranularity.Document,
                CheckSeverity.Defect,
                100,
                CauseAt(context, context.Target.Whole, context.Source.Whole),
                $"cue count {source.Count} in source, {target.Count} in target"));
        }

        var shared = Math.Min(source.Count, target.Count);

        for (var i = 0; i < shared; i++)
        {
            Compare(context, source[i], target[i], "index", "cue index", findings);
            Compare(context, source[i], target[i], "start", "cue start timecode", findings);
            Compare(context, source[i], target[i], "end", "cue end timecode", findings);
            Compare(context, source[i], target[i], "settings", "cue settings", findings);
        }
    }

    private void Compare(CheckContext context, DocumentNode cue, DocumentNode twin, string attribute, string label, List<CheckFinding> findings)
    {
        var expected = cue.Attribute(attribute);
        var actual = twin.Attribute(attribute);

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        findings.Add(Finding(
            twin.Range,
            cue.Range,
            CheckGranularity.Block,
            CheckSeverity.Defect,
            100,
            CauseAt(context, twin.Range, cue.Range),
            $"{label} '{expected}' in source, '{actual}' in target"));
    }
}
