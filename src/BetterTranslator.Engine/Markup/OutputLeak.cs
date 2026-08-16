using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Rejects an answer that contains anything other than a translation. Ported
/// from `Test-OutputLeak`.
///
/// A small model handed a short line and a long instruction block will sometimes
/// answer about the instructions instead of translating. Measured on a real run,
/// three distinct forms of this reached the document: the rules text pasted in as
/// though it were content, narration of the task, and invented explanation with
/// the retry instruction echoed back. None appeared anywhere in the source.
///
/// The prompt check is generated from the prompt rather than from a fixed
/// blacklist: any distinctive run of words that was in the instructions and is
/// now in the answer is a leak, whatever the instructions happen to say. That
/// keeps working when the prompt is edited -- which it now can be, from Settings.
/// </summary>
public static class OutputLeak
{
    /// <summary>Combining marks, zero-width characters, BOM.</summary>
    private static readonly Regex Junk = new(@"[̀-ͯ​-‏﻿]", RegexOptions.Compiled);

    /// <summary>The same character eight times or more.</summary>
    private static readonly Regex Degenerate = new(@"(.)\1{7,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Narration. Anchored at the start, because these only matter as a
    /// preamble; the same words mid-sentence can be a legitimate translation.
    /// </summary>
    private static readonly Regex[] Openers =
    [
        new(@"^To translate\b", Opts), new(@"^The provided text\b", Opts), new(@"^This text\b", Opts),
        new(@"^The text is\b", Opts), new(@"^I will\b", Opts), new(@"^I have\b", Opts),
        new(@"^Here is\b", Opts), new(@"^Here's\b", Opts), new(@"^Sure[,!]", Opts),
        new(@"^Certainly[,!]", Opts), new(@"^Of course[,!]", Opts), new(@"^Note that\b", Opts),
        new(@"^As an AI\b", Opts), new(@"^The user\b", Opts), new(@"^system\s*:", Opts),
    ];

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    /// <summary>
    /// The instructions, rendered in the target language.
    ///
    /// The n-gram test compares against the English prompt, so it cannot see an
    /// answer that TRANSLATED the instructions before returning them. That
    /// happened and reached the document, sitting inside a table cell while
    /// every structural check passed.
    ///
    /// The pattern must be symmetric across both languages: it fires only when
    /// the answer has a meta-word the source lacks, so every term needs its
    /// counterpart on the other side. Listing Czech "instrukc" without English
    /// "instruction" made the gate reject a correct translation of "rather than
    /// sending instructions to the console".
    /// </summary>
    private static readonly Regex Meta = new(
        @"(p[řr]elo[žz]|p[řr]eklad|translat\w*|"
        + @"[čc]e[šs]tin|[čc]esk[éeyáa]|czech|"
        + @"gramatik|grammar|koncovky|case ending|"
        + @"fragment|v[ěe]ty|sentenc\w*|instrukc|instruction\w*)",
        Opts);

    private static readonly Regex NonLetter = new(@"[^\p{L}\s]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public const double DefaultMaxLengthRatio = 1.9;

    /// <summary>Null when the answer is acceptable, otherwise the reason.</summary>
    /// <summary>
    /// Where the word ceiling stops being absolute and becomes proportional.
    ///
    /// Eight is the figure measured over document lines and is the default. A
    /// standalone sentence raises it, because the proportional rule has a cliff
    /// at exactly this count: seven source words allow eighteen, eight allow
    /// eleven. Measured on a real message, "FEATURE: JSON-aware mode for the same
    /// message textbox." is eight words and its correct Czech is twelve -- refused
    /// by one word, for being one word too long to qualify as short.
    /// </summary>
    public const int DefaultProportionalWordFloor = 8;

    /// <param name="proportionalWordFloor">
    /// Below this many source words the ceiling is absolute rather than
    /// proportional. Raise it for standalone sentences; leave it alone for
    /// document chunks, where the proportional rule is what catches invention.
    /// </param>
    public static string? Check(
        string source,
        string? translated,
        string? prompt = null,
        double maxLengthRatio = DefaultMaxLengthRatio,
        int proportionalWordFloor = DefaultProportionalWordFloor)
    {
        ArgumentNullException.ThrowIfNull(source);

        var t = (translated ?? string.Empty).Trim();

        if (t.Length == 0)
        {
            return "empty answer";
        }

        // Degenerate output. Asked to translate "Direct from", the model
        // returned two words followed by sixty combining strikethrough marks.
        // Every gate accepted it: the length was plausible, no markup moved, no
        // instruction words appeared, and the marks are invisible in a terminal.
        if (Junk.IsMatch(t) && !Junk.IsMatch(source))
        {
            return "answer contains combining or invisible characters the source did not have";
        }

        var repeated = Degenerate.Match(t);

        if (repeated.Success && !Degenerate.IsMatch(source))
        {
            return $"answer degenerates into a repeated character: '{repeated.Groups[1].Value}'";
        }

        foreach (var opener in Openers)
        {
            if (opener.IsMatch(t))
            {
                return "answer narrates the task instead of translating: "
                    + $"'{t[..Math.Min(60, t.Length)]}'";
            }
        }

        if (!string.IsNullOrEmpty(prompt))
        {
            var leak = PromptLeak(t, prompt!, source);

            if (leak is not null)
            {
                return leak;
            }
        }

        var meta = Meta.Match(t);

        if (meta.Success && !Meta.IsMatch(source))
        {
            return $"answer contains translation instructions: '{meta.Value}'";
        }

        // Word count, which catches what character length does not. Czech drops
        // articles, so a translation almost always has FEWER words than its
        // English source: measured over 503 lines, 337 came back shorter and
        // only 23 grew past 1.3x. Those 23 are where the damage is.
        //
        // Below eight source words the limit is absolute rather than
        // proportional. A one-word cell is exactly where a model stops
        // translating and starts continuing -- "One" came back as forty words
        // about choosing a domain name -- and it was the one case with nothing
        // in front of it.
        var srcWords = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var outWords = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        var ceiling = srcWords >= proportionalWordFloor
            ? Text.PowerShellCast.ToInt(srcWords * 1.3) + 1
            : srcWords * 2 + 4;

        if (outWords > ceiling)
        {
            return $"answer has {outWords} words for a {srcWords}-word source, content was invented";
        }

        // Length, with a fixed allowance on top of the ratio. A pure ratio
        // punishes short lines for being short: 35 characters of English became
        // 83 of correct Czech -- 237% -- and was refused. Czech carries a roughly
        // constant per-sentence cost, which is an additive term, not a
        // multiplicative one.
        var srcLen = Whitespace.Replace(source, " ").Trim().Length;
        var outLen = Whitespace.Replace(t, " ").Length;

        if (srcLen >= 8 && outLen > Text.PowerShellCast.ToInt(srcLen * maxLengthRatio) + 40)
        {
            var pct = Text.PowerShellCast.ToInt(100.0 * outLen / Math.Max(1, srcLen));
            return $"answer is {pct}% of the source length, content was invented";
        }

        return null;
    }

    /// <summary>
    /// Six-word windows: long enough that ordinary prose does not collide, short
    /// enough to catch a single borrowed clause.
    /// </summary>
    /// <param name="source">
    /// The text being translated, which is itself inside the prompt. A window
    /// drawn from it is not leakage: the answer is SUPPOSED to carry the source's
    /// own tokens where they must survive. Measured, on a real line --
    /// "--from &lt;lang&gt;, --to &lt;lang&gt;, --json, --quiet" strips to
    /// "from lang to lang json quiet", a six-word window present in the prompt
    /// because it is present in the source, so every correct translation of that
    /// line was refused for repeating the instructions.
    /// </param>
    private static string? PromptLeak(string answer, string prompt, string source)
    {
        var promptWords = NonLetter.Replace(prompt, " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var haystack = " " + Whitespace.Replace(NonLetter.Replace(answer, " "), " ") + " ";
        var fromSource = " " + Whitespace.Replace(NonLetter.Replace(source, " "), " ") + " ";

        for (var i = 0; i + 6 <= promptWords.Length; i++)
        {
            var gram = " " + string.Join(' ', promptWords.Skip(i).Take(6)) + " ";

            if (gram.Length < 25 || fromSource.Contains(gram, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (haystack.Contains(gram, StringComparison.OrdinalIgnoreCase))
            {
                return $"answer repeats the instructions: '{gram.Trim()}'";
            }
        }

        return null;
    }
}
