namespace BetterTranslator.Engine.Verification.Structure;

public static class SpliceMap
{
    public static IReadOnlyList<(int Start, int Length)?> Locate(
        string source,
        string target,
        IReadOnlyList<(int Start, int Length, int? SplicedLength)> segments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);

        var located = new (int Start, int Length)?[segments.Count];
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, length, spliced) = segments[i];
            var gap = start - sourceAt;

            if (gap < 0 || start + length > source.Length)
            {
                break;
            }

            if (targetAt + gap > target.Length || !Same(source, sourceAt, target, targetAt, gap))
            {
                break;
            }

            var targetStart = targetAt + gap;
            var nextStart = i + 1 < segments.Count ? segments[i + 1].Start : source.Length;
            var nextGap = source.Substring(start + length, Math.Max(0, nextStart - (start + length)));
            var expected = targetStart + (spliced ?? length);
            int targetEnd;

            if (nextGap.Length == 0)
            {
                targetEnd = i + 1 == segments.Count ? target.Length : Math.Min(expected, target.Length);
            }
            else if (expected + nextGap.Length <= target.Length && Same(nextGap, 0, target, expected, nextGap.Length))
            {
                targetEnd = expected;
            }
            else if (spliced is null)
            {
                targetEnd = target.IndexOf(nextGap, targetStart, StringComparison.Ordinal);
            }
            else
            {
                targetEnd = Nearest(target, nextGap, targetStart, expected);
            }

            if (targetEnd < targetStart)
            {
                break;
            }

            located[i] = (targetStart, targetEnd - targetStart);
            sourceAt = start + length;
            targetAt = targetEnd;
        }

        return located;
    }

    private static int Nearest(string target, string gap, int from, int expected)
    {
        var best = -1;
        var at = from;

        while (at <= target.Length - gap.Length)
        {
            var hit = target.IndexOf(gap, at, StringComparison.Ordinal);

            if (hit < 0)
            {
                break;
            }

            if (best < 0 || Math.Abs(hit - expected) < Math.Abs(best - expected))
            {
                best = hit;
            }

            if (hit > expected)
            {
                break;
            }

            at = hit + 1;
        }

        return best;
    }

    private static bool Same(string a, int aStart, string b, int bStart, int length) =>
        aStart + length <= a.Length
        && bStart + length <= b.Length
        && string.CompareOrdinal(a, aStart, b, bStart, length) == 0;
}
