namespace BetterTranslator.Engine.Languages;

/// <summary>
/// A regex character class for the script a language is written in, or empty
/// when the language gives no signal. Ported from `Get-ScriptPattern`.
///
/// The defect it exists for: verification needs to ask "is this actually in the
/// target language?", and the way it used to ask was a hardcoded class of Czech
/// diacritics. That answers the question for exactly one of the 51 languages in
/// the registry and silently fails the other fifty -- Japanese output contains
/// no c-caron, so a perfect translation was reported as not being in the target
/// language at all.
///
/// The registry already carries the answer without a second table to maintain:
/// the `native` field is each language's own name in its own script. Cestina
/// yields Latin-with-diacritics, Ellinika yields Greek, Russkiy yields Cyrillic,
/// the Japanese entry yields kana and Han. The check follows the registry, so
/// adding a language adds its script too.
///
/// Languages whose own name is plain ASCII -- Deutsch, Bahasa Indonesia --
/// return empty rather than a class that would match anything. There is
/// genuinely no signal, and the caller is expected to skip the check rather than
/// fail it: a wrong answer here is worse than no answer.
/// </summary>
public static class ScriptPattern
{
    /// <summary>
    /// Block ranges paired with the class they contribute. Order matters and is
    /// the original's -- the first block containing a character wins and the
    /// rest are not consulted.
    ///
    /// Two entries deliberately share a class string: Latin-1 Supplement
    /// through Latin Extended-B, and Latin Extended Additional. Deduplication is
    /// by that string, so a name drawing on both still yields one class.
    /// </summary>
    private static readonly (int Lo, int Hi, string Class)[] Blocks =
    [
        (0x00C0, 0x024F, "À-ɏḀ-ỿ"),          // Latin with diacritics
        (0x0370, 0x03FF, "Ͱ-Ͽἀ-῿"),          // Greek
        (0x0400, 0x04FF, "Ѐ-ӿ"),                       // Cyrillic
        (0x0530, 0x058F, "԰-֏"),                       // Armenian
        (0x0590, 0x05FF, "֐-׿"),                       // Hebrew
        (0x0600, 0x06FF, "؀-ۿݐ-ݿ"),          // Arabic
        (0x0900, 0x097F, "ऀ-ॿ"),                       // Devanagari
        (0x0980, 0x09FF, "ঀ-৿"),                       // Bengali
        (0x0E00, 0x0E7F, "฀-๿"),                       // Thai
        (0x10A0, 0x10FF, "Ⴀ-ჿ"),                       // Georgian
        (0x1E00, 0x1EFF, "À-ɏḀ-ỿ"),          // Latin extended additional
        (0x3040, 0x30FF, "぀-ヿ一-鿿"),          // Kana, with Han alongside it
        (0x4E00, 0x9FFF, "一-鿿"),                       // Han
        (0xAC00, 0xD7AF, "가-힯ᄀ-ᇿ"),          // Hangul
    ];

    public static string For(LanguageEntry? language) => For(language?.Native);

    /// <summary>
    /// Built from the native name one character at a time. Anything below
    /// U+00C0 is ASCII or Latin-1 punctuation and carries no script signal, so
    /// it is skipped rather than treated as Latin.
    /// </summary>
    public static string For(string? native)
    {
        if (string.IsNullOrEmpty(native))
        {
            return string.Empty;
        }

        var classes = new List<string>();

        foreach (var ch in native)
        {
            int cp = ch;

            if (cp < 0x00C0)
            {
                continue;
            }

            foreach (var (lo, hi, cls) in Blocks)
            {
                if (cp >= lo && cp <= hi)
                {
                    if (!classes.Contains(cls, StringComparer.Ordinal))
                    {
                        classes.Add(cls);
                    }

                    break;
                }
            }
        }

        return classes.Count == 0 ? string.Empty : "[" + string.Concat(classes) + "]";
    }
}
