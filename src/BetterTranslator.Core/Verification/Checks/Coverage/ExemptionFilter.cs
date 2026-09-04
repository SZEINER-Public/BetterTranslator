namespace BetterTranslator.Core.Verification.Checks.Coverage;

public static class ExemptionFilter
{
    public static bool Honored(DocumentModel target, ExemptSpan span)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(span);

        if (span.Range.Offset < 0 || span.Range.End > target.Length)
        {
            return false;
        }

        if (span.Text.Length == 0 || target.Text.Length < target.Length)
        {
            return true;
        }

        var actual = CoverageText.Slice(target, span.Range);

        return span.Inflectable
            ? actual.StartsWith(Stem(span.Text), StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, span.Text, StringComparison.Ordinal);
    }

    public static IReadOnlyList<ExemptSpan> HonoredSpans(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [.. context.Exemptions.Spans.Where(span => Honored(context.Target, span))];
    }

    public static bool Shields(CheckContext context, CheckRange range)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(range);

        return HonoredSpans(context).Any(span => span.Range.Contains(range));
    }

    private static string Stem(string text)
    {
        var length = Math.Max(1, text.Length - Math.Min(3, text.Length / 3));
        return text[..length];
    }
}
