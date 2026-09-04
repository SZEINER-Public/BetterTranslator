namespace BetterTranslator.Core.Verification.Checks.Coverage;

public enum CoverageTokenKind
{
    Word,
    Number,
    Punctuation,
}

public sealed record CoverageToken(CoverageTokenKind Kind, string Text, CheckRange Range)
{
    public string Key => Kind == CoverageTokenKind.Word ? Text.ToLowerInvariant() : Text;
}

public static class CoverageTokenizer
{
    public static IReadOnlyList<CoverageToken> Tokenize(string text, CheckRange range)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(range);

        var tokens = new List<CoverageToken>();
        var end = Math.Min(text.Length, range.End);
        var i = Math.Max(0, range.Offset);

        while (i < end)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;

            if (char.IsLetter(c))
            {
                while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || IsInnerApostrophe(text, i, end)))
                {
                    i++;
                }

                tokens.Add(new CoverageToken(CoverageTokenKind.Word, text[start..i], new CheckRange(range.UnitPath, start, i - start)));
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < end && (char.IsDigit(text[i]) || IsInnerNumberSeparator(text, i, end)))
                {
                    i++;
                }

                tokens.Add(new CoverageToken(CoverageTokenKind.Number, text[start..i], new CheckRange(range.UnitPath, start, i - start)));
                continue;
            }

            while (i < end && !char.IsWhiteSpace(text[i]) && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            tokens.Add(new CoverageToken(CoverageTokenKind.Punctuation, text[start..i], new CheckRange(range.UnitPath, start, i - start)));
        }

        return tokens;
    }

    private static bool IsInnerApostrophe(string text, int i, int end) =>
        text[i] is '\'' or '’' && i + 1 < end && char.IsLetter(text[i + 1]) && char.IsLetter(text[i - 1]);

    private static bool IsInnerNumberSeparator(string text, int i, int end) =>
        text[i] is '.' or ',' or ':' && i + 1 < end && char.IsDigit(text[i + 1]) && char.IsDigit(text[i - 1]);
}
