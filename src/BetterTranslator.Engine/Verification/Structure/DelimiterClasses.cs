using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Engine.Verification.Structure;

public static class DelimiterClasses
{
    public static DelimiterClass Left(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);

        return offset <= 0 ? DelimiterClass.Start : Of(text[offset - 1]);
    }

    public static DelimiterClass Right(string text, int end)
    {
        ArgumentNullException.ThrowIfNull(text);

        return end >= text.Length ? DelimiterClass.End : Of(text[end]);
    }

    public static DelimiterClass Of(char character)
    {
        if (char.IsWhiteSpace(character))
        {
            return DelimiterClass.Whitespace;
        }

        if (char.IsDigit(character))
        {
            return DelimiterClass.Digit;
        }

        if (char.IsLetter(character))
        {
            return DelimiterClass.Letter;
        }

        return DelimiterClass.Punctuation;
    }

    public static bool JoinsWord(DelimiterClass delimiter) =>
        delimiter is DelimiterClass.Letter or DelimiterClass.Digit;
}
