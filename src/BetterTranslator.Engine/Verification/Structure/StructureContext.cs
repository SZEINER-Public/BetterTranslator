using System.Text.RegularExpressions;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Subtitles;

namespace BetterTranslator.Engine.Verification.Structure;

public static class StructureContext
{
    public static IFormatAdapter AdapterFor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (JsonSyntax.LooksLikeJson(text))
        {
            return JsonStructure.Instance;
        }

        if (SubtitleSyntax.LooksLikeSubtitle(text))
        {
            return SubtitleStructure.Instance;
        }

        return MarkdownSyntax.HasStructure(text) ? MarkdownStructure.Instance : ProseStructure.Instance;
    }

    public static IFormatAdapter AdapterFor(ContentShape shape) => shape switch
    {
        ContentShape.Json => JsonStructure.Instance,
        ContentShape.Markdown => MarkdownStructure.Instance,
        _ => ProseStructure.Instance,
    };

    public static CheckContext Build(
        string source,
        string target,
        IFormatAdapter? adapter = null,
        IReadOnlyList<SegmentTrace>? traces = null,
        IEnumerable<ExemptSpan>? exemptions = null,
        CheckRunSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        adapter ??= AdapterFor(source);

        var sourceModel = adapter.Read(source);
        var targetModel = adapter.Read(target);
        var registry = new ExemptionRegistry();

        foreach (var span in exemptions ?? [])
        {
            registry.Add(span);
        }

        var alignment = Align(source, target, sourceModel, traces ?? [], registry);

        return new CheckContext
        {
            Source = sourceModel,
            Target = targetModel,
            Alignment = alignment,
            Exemptions = registry,
            Adapter = adapter,
            Settings = settings ?? new CheckRunSettings(),
        };
    }

    private static IReadOnlyList<SegmentAlignment> Align(
        string source,
        string target,
        DocumentModel sourceModel,
        IReadOnlyList<SegmentTrace> traces,
        ExemptionRegistry registry)
    {
        if (traces.Count == 0)
        {
            return [];
        }

        var ordered = traces
            .Select((trace, index) => (Trace: trace, Index: index))
            .OrderBy(t => t.Trace.SourceStart)
            .ThenBy(t => t.Index)
            .Select(t => t.Trace)
            .ToList();

        var located = SpliceMap.Locate(
            source,
            target,
            [.. ordered.Select(t => (t.SourceStart, t.SourceLength, (int?)(t.TargetLength ?? t.Spliced?.Length)))]);

        var alignment = new List<SegmentAlignment>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var trace = ordered[i];
            var chunk = sourceModel.Chunks.FirstOrDefault(c => c.Range.Offset <= trace.SourceStart && trace.SourceStart + trace.SourceLength <= c.Range.End);
            var unitPath = chunk?.Range.UnitPath ?? DocumentModel.RootPath;
            var sourceRange = new CheckRange(unitPath, trace.SourceStart, trace.SourceLength);

            CheckRange? targetRange = null;

            if (trace.TargetStart is { } explicitStart)
            {
                targetRange = new CheckRange(unitPath, explicitStart, trace.TargetLength ?? trace.SourceLength);
            }
            else if (located[i] is { } found)
            {
                targetRange = new CheckRange(unitPath, found.Start, found.Length);
            }

            var masks = trace.Outcome == SegmentOutcome.Translated && trace.Answer is not null
                ? Masks(source, target, trace, sourceRange, targetRange)
                : [];

            foreach (var mask in masks)
            {
                if (mask.TargetRange is not null)
                {
                    registry.Add(new ExemptSpan(mask.TargetRange, mask.Reason, mask.Original));
                }
            }

            var identity = (chunk?.Identity ?? "segment") + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            alignment.Add(new SegmentAlignment(identity, sourceRange, targetRange, trace.Outcome, masks));
        }

        return alignment;
    }

    private static List<MaskRecord> Masks(
        string source,
        string target,
        SegmentTrace trace,
        CheckRange sourceRange,
        CheckRange? targetRange)
    {
        var masks = trace.MaskList;

        if (masks.Count == 0)
        {
            return [];
        }

        var answer = trace.Answer ?? string.Empty;
        var normalized = PlaceholderGuard.Normalize(answer) ?? string.Empty;
        var intact = MarkupGuard.SentinelPattern.Matches(answer).Select(m => m.Value).ToList();
        var returned = MarkupGuard.SentinelPattern.Matches(normalized).Cast<Match>().ToList();

        var sourceText = source.Substring(sourceRange.Offset, sourceRange.Length);
        var claimed = new List<(int Start, int End)>();
        var sourceAt = new int[masks.Count];

        for (var i = 0; i < masks.Count; i++)
        {
            sourceAt[i] = FindUnclaimed(sourceText, masks[i].Original, claimed);
        }

        var targetAt = new int[masks.Count];
        Array.Fill(targetAt, -1);

        if (targetRange is not null)
        {
            var targetText = target.Substring(targetRange.Offset, targetRange.Length);
            var cursor = 0;
            var placed = new HashSet<int>();
            var taken = new List<(int Start, int End)>();

            foreach (var match in returned)
            {
                var ordinal = masks.ToList().FindIndex(m => string.Equals(m.Sentinel, match.Value, StringComparison.Ordinal));

                if (ordinal < 0 || placed.Contains(ordinal))
                {
                    continue;
                }

                var hit = FindUnclaimed(targetText, masks[ordinal].Original, taken, cursor);

                if (hit < 0)
                {
                    hit = FindUnclaimed(targetText, masks[ordinal].Original, taken);
                }

                if (hit >= 0)
                {
                    targetAt[ordinal] = hit;
                    placed.Add(ordinal);
                    cursor = hit + masks[ordinal].Original.Length;
                }
            }
        }

        var records = new List<MaskRecord>();

        for (var i = 0; i < masks.Count; i++)
        {
            var mask = masks[i];
            var count = returned.Count(m => string.Equals(m.Value, mask.Sentinel, StringComparison.Ordinal));
            var intactCount = intact.Count(v => string.Equals(v, mask.Sentinel, StringComparison.Ordinal));

            var maskSource = sourceAt[i] >= 0
                ? new CheckRange(sourceRange.UnitPath, sourceRange.Offset + sourceAt[i], mask.Original.Length)
                : sourceRange;

            CheckRange? maskTarget = targetRange is not null && targetAt[i] >= 0
                ? new CheckRange(targetRange.UnitPath, targetRange.Offset + targetAt[i], mask.Original.Length)
                : null;

            records.Add(new MaskRecord(
                i,
                mask.Sentinel,
                mask.Original,
                mask.Reason,
                maskSource,
                maskTarget,
                count,
                count > 0 && intactCount == count,
                sourceAt[i] >= 0,
                DelimiterClasses.Left(source, maskSource.Offset),
                DelimiterClasses.Right(source, maskSource.End),
                maskTarget is null ? DelimiterClass.Start : DelimiterClasses.Left(target, maskTarget.Offset),
                maskTarget is null ? DelimiterClass.End : DelimiterClasses.Right(target, maskTarget.End)));
        }

        return records;
    }

    private static int FindUnclaimed(string text, string needle, List<(int Start, int End)> claimed, int from = 0)
    {
        if (needle.Length == 0)
        {
            return -1;
        }

        var at = from;

        while (at <= text.Length - needle.Length)
        {
            var hit = text.IndexOf(needle, at, StringComparison.Ordinal);

            if (hit < 0)
            {
                return -1;
            }

            var end = hit + needle.Length;

            if (!claimed.Any(c => hit < c.End && c.Start < end))
            {
                claimed.Add((hit, end));
                return hit;
            }

            at = hit + 1;
        }

        return -1;
    }
}
