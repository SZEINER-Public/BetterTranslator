using System.Text.RegularExpressions;

namespace BetterTranslator.Bench;

internal static partial class Placeholders
{
    [GeneratedRegex(@"\{\{[^}]*\}\}|\{[A-Za-z0-9_]+\}|:[A-Za-z_][A-Za-z0-9_]*|%[sd]|\$[A-Za-z_][A-Za-z0-9_]*|<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    internal static IReadOnlyList<string> In(string value) => Token().Matches(value).Select(match => match.Value).ToList();

    internal static SortedDictionary<string, int> Multiset(string value)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in In(value))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return counts;
    }

    internal static bool SameMultiset(string source, string target)
    {
        var left = Multiset(source);
        var right = Multiset(target);

        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    internal static string Describe(string value)
    {
        var multiset = Multiset(value);

        return multiset.Count == 0 ? "none" : string.Join(" ", multiset.Select(pair => $"{pair.Key}x{pair.Value}"));
    }
}
