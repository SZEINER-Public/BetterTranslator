namespace BetterTranslator.Core.Verification.Checks.Coverage;

public abstract class CoverageCheck : ICheck
{
    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Coverage.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<CheckFinding>();
        Find(context, CoverageAlignment.Of(context), findings);

        return
        [
            .. findings
                .Where(finding => !ExemptionFilter.Shields(context, finding.TargetRange))
                .OrderBy(finding => finding.TargetRange.Offset)
                .ThenBy(finding => finding.TargetRange.Length)
                .ThenBy(finding => finding.Evidence, StringComparer.Ordinal),
        ];
    }

    protected abstract void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings);

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

    protected static string CauseOf(UnitPair pair)
    {
        if (!pair.FromTrace)
        {
            return CheckCause.AdapterRebuild;
        }

        return pair.Outcome switch
        {
            SegmentOutcome.Translated or SegmentOutcome.Recovered or SegmentOutcome.Kept => CheckCause.ModelOutput,
            _ => CheckCause.Segmentation,
        };
    }

    protected static CheckGranularity AtLeast(CheckGranularity ceiling, CheckGranularity floor) =>
        ceiling > floor ? ceiling : floor;

    protected static CheckRange GapAround(CoverageAlignmentResult alignment, int index, DocumentModel target)
    {
        var pairs = alignment.Pairs;
        var previous = -1;
        var next = -1;

        for (var i = index - 1; i >= 0; i--)
        {
            if (pairs[i].TargetRange is not null)
            {
                previous = i;
                break;
            }
        }

        for (var i = index + 1; i < pairs.Count; i++)
        {
            if (pairs[i].TargetRange is not null)
            {
                next = i;
                break;
            }
        }

        var start = previous >= 0 ? pairs[previous].TargetRange!.End : 0;
        var end = next >= 0 ? pairs[next].TargetRange!.Offset : target.Length;
        var path = next >= 0 ? pairs[next].TargetRange!.UnitPath
            : previous >= 0 ? pairs[previous].TargetRange!.UnitPath
            : DocumentModel.RootPath;

        return new CheckRange(path, Math.Min(start, end), Math.Max(0, end - start));
    }

    protected static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 60 ? flat : flat[..57] + "...";
    }
}
