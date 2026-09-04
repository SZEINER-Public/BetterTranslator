namespace BetterTranslator.Core.Verification.Checks.Ratio;

public sealed record RatioUnit(
    string Identity,
    string UnitType,
    CheckRange SourceRange,
    CheckRange TargetRange,
    string SourceText,
    string TargetText,
    SegmentOutcome Outcome,
    bool FromTrace);

public static class RatioUnits
{
    public static string PairKey(CheckRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.SourceLanguage) || string.IsNullOrWhiteSpace(settings.TargetLanguage))
        {
            return string.Empty;
        }

        return Primary(settings.SourceLanguage) + "-" + Primary(settings.TargetLanguage);
    }

    public static string UnitTypeOf(string format, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(sourceText);

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return RatioUnitType.Value;
        }

        if (string.Equals(format, "subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return RatioUnitType.Cue;
        }

        var words = RatioMeasures.Words(sourceText).Count;

        if (words <= 1)
        {
            return RatioUnitType.Word;
        }

        return RatioMeasures.SentenceCount(sourceText) <= 1 ? RatioUnitType.Sentence : RatioUnitType.Block;
    }

    public static IReadOnlyList<RatioUnit> Of(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var units = new List<RatioUnit>();

        if (context.Alignment.Count > 0)
        {
            foreach (var segment in context.Alignment.OrderBy(s => s.SourceRange.Offset).ThenBy(s => s.Identity, StringComparer.Ordinal))
            {
                if (segment.TargetRange is null || segment.Outcome is SegmentOutcome.Dropped or SegmentOutcome.Stopped)
                {
                    continue;
                }

                var hiddenSource = segment.Masks.Where(m => m.SourceLocated).Select(m => m.SourceRange).ToList();
                var hiddenTarget = segment.Masks.Where(m => m.TargetRange is not null).Select(m => m.TargetRange!).ToList();

                Add(context, units, segment.Identity, segment.SourceRange, segment.TargetRange, segment.Outcome, hiddenSource, hiddenTarget, true);
            }

            return units;
        }

        var source = NonBlank(context.Source);
        var target = NonBlank(context.Target);

        if (source.Count != target.Count)
        {
            return units;
        }

        for (var i = 0; i < source.Count; i++)
        {
            var identity = source[i].Identity + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Add(context, units, identity, source[i].Range, target[i].Range, SegmentOutcome.Translated, [], [], false);
        }

        return units;
    }

    private static void Add(
        CheckContext context,
        List<RatioUnit> units,
        string identity,
        CheckRange sourceRange,
        CheckRange targetRange,
        SegmentOutcome outcome,
        List<CheckRange> hiddenSource,
        List<CheckRange> hiddenTarget,
        bool fromTrace)
    {
        var exempt = context.Exemptions.Spans.Where(s => s.Range.Overlaps(targetRange)).ToList();
        hiddenTarget.AddRange(exempt.Select(s => s.Range));

        var sourceText = Visible(context.Source.Text, sourceRange, hiddenSource);
        sourceText = RemoveTexts(sourceText, exempt.Select(s => s.Text));
        var targetText = Visible(context.Target.Text, targetRange, hiddenTarget);

        if (RatioMeasures.Collapse(sourceText).Length == 0)
        {
            return;
        }

        units.Add(new RatioUnit(
            identity,
            UnitTypeOf(context.Source.Format, sourceText),
            sourceRange,
            targetRange,
            sourceText,
            targetText,
            outcome,
            fromTrace));
    }

    private static List<ChunkNode> NonBlank(DocumentModel model)
    {
        var chunks = model.Chunks.Where(c => Slice(model.Text, c.Range).Any(ch => !char.IsWhiteSpace(ch))).ToList();

        if (chunks.Count > 0 || model.Text.All(char.IsWhiteSpace))
        {
            return chunks;
        }

        return [new ChunkNode("document", "document", model.Whole)];
    }

    private static string Slice(string text, CheckRange range)
    {
        var offset = Math.Clamp(range.Offset, 0, text.Length);
        var length = Math.Clamp(range.Length, 0, text.Length - offset);

        return text.Substring(offset, length);
    }

    private static string Visible(string text, CheckRange range, IReadOnlyList<CheckRange> hidden)
    {
        var chars = Slice(text, range).ToCharArray();

        foreach (var span in hidden)
        {
            for (var i = Math.Max(span.Offset, range.Offset); i < Math.Min(span.End, range.End); i++)
            {
                chars[i - range.Offset] = ' ';
            }
        }

        return new string(chars);
    }

    private static string RemoveTexts(string text, IEnumerable<string> needles)
    {
        var result = text;

        foreach (var needle in needles.Where(n => n.Length > 0).OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal))
        {
            var at = result.IndexOf(needle, StringComparison.Ordinal);

            if (at >= 0)
            {
                result = result[..at] + new string(' ', needle.Length) + result[(at + needle.Length)..];
            }
        }

        return result;
    }

    private static string Primary(string language) => language.Trim().Split('-', '_')[0].ToLowerInvariant();
}
