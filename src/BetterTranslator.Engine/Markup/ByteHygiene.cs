using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

public static class ByteHygiene
{
    private static readonly (Regex Class, string Ascii)[] Lookalikes =
    [
        (new Regex("[‘’‚‛‹›′]", RegexOptions.Compiled), "'"),
        (new Regex("[“”„‟«»″]", RegexOptions.Compiled), "\""),
        (new Regex("[‐‑‒–—―]", RegexOptions.Compiled), "-"),
        (new Regex("…", RegexOptions.Compiled), "..."),
    ];

    private static readonly Regex TrailingBlank = new(@"[ \t]+$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Restore(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        if (!Config.PipelineOptions.RestoreAsciiPunctuation)
        {
            return translated;
        }

        var text = translated;

        foreach (var (lookalike, ascii) in Lookalikes)
        {
            if (!lookalike.IsMatch(source))
            {
                text = lookalike.Replace(text, ascii);
            }
        }

        return TrailingBlank.IsMatch(source) ? text : TrailingBlank.Replace(text, string.Empty);
    }

    private static readonly Regex LeadingListMarker =
        new(@"^[ \t]*([-*+]|\d+[.)])[ \t]+", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AnyAsterisk = new(@"\*", RegexOptions.Compiled);

    public static string DropInventedMarkup(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        if (!Config.PipelineOptions.DropInventedMarkup)
        {
            return translated;
        }

        var text = translated;

        if (!LeadingListMarker.IsMatch(source))
        {
            text = LeadingListMarker.Replace(text, string.Empty);
        }

        if (!AnyAsterisk.IsMatch(source))
        {
            text = AnyAsterisk.Replace(text, string.Empty);
        }

        return text;
    }

    public static string TrimIntroducedTrailingBlanks(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        if (!Config.PipelineOptions.TrimIntroducedTrailingBlanks || TrailingBlank.IsMatch(source))
        {
            return translated;
        }

        return TrailingBlank.Replace(translated, string.Empty);
    }
}
