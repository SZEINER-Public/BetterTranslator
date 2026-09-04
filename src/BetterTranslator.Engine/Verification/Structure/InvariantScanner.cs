using System.Text.RegularExpressions;
using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Engine.Verification.Structure;

public static partial class InvariantScanner
{
    private static readonly (InvariantClass Class, Regex Pattern)[] Patterns =
    [
        (InvariantClass.Url, UrlPattern),
        (InvariantClass.FilePath, FilePathPattern),
        (InvariantClass.Date, DatePattern),
        (InvariantClass.Version, VersionPattern),
        (InvariantClass.Currency, CurrencyPattern),
        (InvariantClass.Identifier, IdentifierPattern),
        (InvariantClass.Number, NumberPattern),
    ];

    [GeneratedRegex(@"(?:https?|ftp)://[^\s<>()\[\]""']+|(?<![\w.])www\.[^\s<>()\[\]""']+")]
    private static partial Regex UrlPattern { get; }

    [GeneratedRegex(@"(?<![\w:\\/])(?:[A-Za-z]:\\|\\\\|~[/\\]|\.{1,2}[/\\]|/(?=[\w.-]+/))(?:[\w.~-]+[/\\])*[\w.~-]*|(?<![\w/.-])[\w.-]+(?:/[\w.-]+)+\.[A-Za-z0-9]{1,8}(?![\w/])")]
    private static partial Regex FilePathPattern { get; }

    [GeneratedRegex(@"(?<!\d)(?:\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?|\d{1,2}[./]\s?\d{1,2}[./]\s?\d{4}|\d{1,2}/\d{1,2}/\d{2})(?!\d)")]
    private static partial Regex DatePattern { get; }

    [GeneratedRegex(@"(?<![\w.])v?\d+(?:\.\d+){2,}(?:[-+][\w.]+)?(?!\w|\.\w)|(?<![\w.])v\d+(?:\.\d+)+(?!\w|\.\w)")]
    private static partial Regex VersionPattern { get; }

    [GeneratedRegex(@"(?<![\w.])(?:[$€£¥]\s?\d[\d.,\s]*\d|\d[\d.,\s]*\d\s?(?:Kč|USD|EUR|CZK|GBP|€|\$|£)|[$€£¥]\d)(?![\w])")]
    private static partial Regex CurrencyPattern { get; }

    [GeneratedRegex(@"(?<![\w-])--?[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)+(?![\w-])|(?<![\w-])--[A-Za-z][A-Za-z0-9]*(?![\w-])|(?<![\w.])[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+(?!\w|\.\w)|(?<![\w])[a-z]+[A-Z][\p{L}\p{Nd}]*(?![\w])|(?<![\w])[A-Za-z][A-Za-z0-9]*_[\p{L}\p{Nd}_]+(?![\w])|(?<![\w])[A-Z][a-z]+(?:[A-Z][\p{Ll}\p{Nd}]+)+(?![\w])")]
    private static partial Regex IdentifierPattern { get; }

    [GeneratedRegex(@"(?<![\w.,])\d+(?:[.,]\d+)*(?![\w.,]?\d)")]
    private static partial Regex NumberPattern { get; }

    public static IReadOnlyList<InvariantToken> Scan(string text, string unitPath, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(unitPath);

        if (text.Length == 0)
        {
            return [];
        }

        var found = new List<InvariantToken>();
        var claimed = new bool[text.Length];

        foreach (var (kind, pattern) in Patterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Length == 0 || Claimed(claimed, match.Index, match.Length))
                {
                    continue;
                }

                for (var i = match.Index; i < match.Index + match.Length; i++)
                {
                    claimed[i] = true;
                }

                found.Add(new InvariantToken(kind, match.Value, new CheckRange(unitPath, offset + match.Index, match.Length)));
            }
        }

        found.Sort((a, b) => a.Range.Offset.CompareTo(b.Range.Offset));
        return found;
    }

    private static bool Claimed(bool[] claimed, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (claimed[i])
            {
                return true;
            }
        }

        return false;
    }
}
