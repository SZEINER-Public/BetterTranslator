using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Whether an answer is a translation at all.
///
/// The sentinel checks pass an answer that echoed its input, because echoing it
/// carries every sentinel in order. That is the one failure the structural gates
/// cannot see, and it is the one that puts source-language text under a
/// target-language heading. Decided by comparing the answer with the unit it
/// answers, so no word list and no language identification is involved.
/// </summary>
public static class UnitFidelity
{
    private const int ResidueRunWords = 4;

    private static readonly Regex Sentinel = new(@"\[\[(\d+)\]\]", RegexOptions.Compiled);
    private static readonly Regex Collapse = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static bool Echoed(string unit, string? answer)
    {
        ArgumentNullException.ThrowIfNull(unit);

        return answer is not null && string.Equals(Comparable(unit), Comparable(answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// The longest run of consecutive source words the answer repeats verbatim.
    /// Reported rather than refused: a unit that came back half translated still
    /// holds translated text, and refusing it keeps the whole source instead.
    /// </summary>
    public static int LongestSourceRun(string unit, string? answer)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (answer is null)
        {
            return 0;
        }

        var source = Words(unit);
        var target = Words(answer);

        if (source.Count == 0 || target.Count == 0)
        {
            return 0;
        }

        var longest = 0;
        var lengths = new int[source.Count + 1, target.Count + 1];

        for (var s = 1; s <= source.Count; s++)
        {
            for (var t = 1; t <= target.Count; t++)
            {
                if (!string.Equals(source[s - 1], target[t - 1], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lengths[s, t] = lengths[s - 1, t - 1] + 1;

                if (lengths[s, t] > longest)
                {
                    longest = lengths[s, t];
                }
            }
        }

        return longest;
    }

    public static bool Unverified(string unit, string? answer) =>
        Echoed(unit, answer) || LongestSourceRun(unit, answer) >= ResidueRunWords;

    private static string Comparable(string text) =>
        Collapse.Replace(Sentinel.Replace(text, string.Empty), " ").Trim();

    private static List<string> Words(string text) =>
        [.. Word.Matches(Sentinel.Replace(text, " ")).Select(match => match.Value)];
}
