namespace BetterTranslator.Core.Verification.Checks.Structure;

public static class SequenceDiff
{
    public enum Op
    {
        Match,
        Delete,
        Insert,
    }

    private const int MaxCells = 4_000_000;

    public static IReadOnlyList<(Op Op, int SourceIndex, int TargetIndex)> Diff(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if ((long)(source.Count + 1) * (target.Count + 1) > MaxCells)
        {
            return Pairwise(source, target);
        }

        var lengths = new int[source.Count + 1, target.Count + 1];

        for (var i = source.Count - 1; i >= 0; i--)
        {
            for (var j = target.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(source[i], target[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var edits = new List<(Op, int, int)>();
        var s = 0;
        var t = 0;

        while (s < source.Count && t < target.Count)
        {
            if (string.Equals(source[s], target[t], StringComparison.Ordinal))
            {
                edits.Add((Op.Match, s, t));
                s++;
                t++;
            }
            else if (lengths[s + 1, t] >= lengths[s, t + 1])
            {
                edits.Add((Op.Delete, s, t));
                s++;
            }
            else
            {
                edits.Add((Op.Insert, s, t));
                t++;
            }
        }

        while (s < source.Count)
        {
            edits.Add((Op.Delete, s, t));
            s++;
        }

        while (t < target.Count)
        {
            edits.Add((Op.Insert, s, t));
            t++;
        }

        return edits;
    }

    private static List<(Op, int, int)> Pairwise(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        var edits = new List<(Op, int, int)>();
        var shared = Math.Min(source.Count, target.Count);

        for (var i = 0; i < shared; i++)
        {
            if (string.Equals(source[i], target[i], StringComparison.Ordinal))
            {
                edits.Add((Op.Match, i, i));
            }
            else
            {
                edits.Add((Op.Delete, i, i));
                edits.Add((Op.Insert, i, i));
            }
        }

        for (var i = shared; i < source.Count; i++)
        {
            edits.Add((Op.Delete, i, target.Count));
        }

        for (var i = shared; i < target.Count; i++)
        {
            edits.Add((Op.Insert, source.Count, i));
        }

        return edits;
    }
}
