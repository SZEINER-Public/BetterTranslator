using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>Protected text and the originals lifted out of it.</summary>
public sealed record ProtectedText(string Text, IReadOnlyList<string> Store);

/// <summary>
/// Keeps a translated document structurally identical to its source. Ported from
/// `scripts\markup-guard.ps1`.
///
/// This exists because a real run did the following to a 48 KB README: 935 lines
/// became 650, 124 table rows became 84, and 44 code fences became 37 -- an odd
/// number, so every fence after the orphan rendered as one giant code block. The
/// model was not at fault. Nothing protected the markup and nothing checked the
/// result.
///
/// Two mechanisms, in this order, because the second alone is not enough.
/// PROTECT lifts out what is catastrophic to lose and replaces it with a short
/// sentinel before the text is ever sent, so the model cannot translate a flag,
/// drop a fence, or reflow a row it was not shown. VERIFY is
/// <see cref="ChunkIntegrity"/>, which checks that everything that went out came
/// back.
/// </summary>
public static class MarkupGuard
{
    /// <summary>
    /// Sentinel form: [[7]]. Deliberately minimal and numeric -- anything
    /// containing a word (CODE, BLOCK) invites the model to translate it.
    /// Brackets plus digits survive every model tested and cannot collide with
    /// Markdown syntax.
    /// </summary>
    internal static readonly Regex SentinelPattern = new(@"\[\[(\d+)\]\]", RegexOptions.Compiled);

    /// <summary>
    /// Protect sparingly. This list was cut down after measurement.
    ///
    /// The first version also protected inline code spans, long options and whole
    /// table rows. On a 48 KB README that produced 544 sentinels -- roughly fifty
    /// per chunk -- and a 9B model could not echo fifty opaque tokens in order, so
    /// every chunk failed integrity and was kept as source. Nothing was corrupted,
    /// but nothing was translated either. Worse, protecting a whole table row meant
    /// the prose inside its cells could never be translated at all.
    ///
    /// So: protect what is catastrophic to lose AND contains no translatable
    /// prose. Everything else is counted by <see cref="ChunkIntegrity"/> instead
    /// of hidden.
    ///
    /// Order is load-bearing -- sentinels are numbered as they are taken, so
    /// reordering this array renumbers every sentinel.
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        // Fenced code block, including the fence lines. No prose inside, and a
        // lost fence corrupts every following section.
        new("(?ms)^```[^\n]*\n.*?^```[ \t]*$", RegexOptions.Compiled),

        // HTML comment blocks
        new("(?s)<!--.*?-->", RegexOptions.Compiled),

        // Bare URL: never translatable, and a mangled one is a dead link
        new(@"https?://[^\s)>\]]+", RegexOptions.Compiled),

        // Windows path
        new(@"(?<![A-Za-z0-9])[A-Za-z]:\\[^\s`'""]+", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Lifts protected spans out and leaves a sentinel in each place.
    ///
    /// One method rather than the original's pair. `Protect-Markup` builds a
    /// local store, writes to a script-scoped one instead because a PowerShell
    /// replace callback cannot close over a local, and then returns the empty
    /// local -- so only `Protect-MarkupSafe`, which reads the script-scoped store
    /// back, ever returned anything usable. A closure has no such problem here,
    /// so the working behaviour is the only behaviour.
    /// </summary>
    public static ProtectedText Protect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var store = new List<string>();
        var output = text;

        foreach (var pattern in Patterns)
        {
            output = pattern.Replace(output, match =>
            {
                store.Add(match.Value);
                return $"[[{store.Count - 1}]]";
            });
        }

        return new ProtectedText(output, store);
    }

    /// <summary>
    /// Puts the originals back, byte for byte. Highest index first, so [[10]] is
    /// not partially matched by [[1]].
    /// </summary>
    public static string Restore(string text, IReadOnlyList<string> store)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(store);

        var output = text;

        for (var i = store.Count - 1; i >= 0; i--)
        {
            output = output.Replace($"[[{i}]]", store[i], StringComparison.Ordinal);
        }

        return output;
    }

    /// <summary>Every sentinel id present, in ascending numeric order.</summary>
    public static IReadOnlyList<int> SentinelIds(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var ids = SentinelPattern.Matches(text)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        ids.Sort();
        return ids;
    }

    /// <summary>
    /// The placeholder forms, in the order they are consumed. The colon form
    /// needs a lookbehind: a bare ':[A-Za-z_]\w*' fired on '::WriteAllText' in
    /// [System.IO.File]::WriteAllText and on '-Confirm:$false', so prose about
    /// .NET APIs manufactured placeholders the model could not possibly reproduce
    /// and the chunk was rejected for a defect that was never there. Excluding a
    /// preceding colon, word character or dollar keeps ':user' in 'Welcome :user'
    /// while ignoring both of those.
    /// </summary>
    private static readonly Regex[] PlaceholderPatterns =
    [
        new(@"\{\{\s*[^}]+\s*\}\}", RegexOptions.Compiled),
        new(@"\{[A-Za-z0-9_.]+\}", RegexOptions.Compiled),
        new(@"\{[0-9]+\}", RegexOptions.Compiled),
        new(@"(?<![A-Za-z0-9_:$]):[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled),
        new("%[sdfx]", RegexOptions.Compiled),
    ];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Every placeholder in the text, sorted.
    ///
    /// A match is blanked to spaces of the same length rather than removed, so a
    /// later pattern cannot re-match inside an earlier one's text and the offsets
    /// of everything after it stay put.
    /// </summary>
    public static IReadOnlyList<string> Placeholders(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<string>();
        var remaining = text;

        foreach (var pattern in PlaceholderPatterns)
        {
            remaining = pattern.Replace(remaining, match =>
            {
                found.Add(Whitespace.Replace(match.Value, string.Empty));
                return new string(' ', match.Value.Length);
            });
        }

        found.Sort(Order);
        return found;
    }

    /// <summary>
    /// A deliberate, documented divergence from the reference -- the only one in
    /// this module.
    ///
    /// `Sort-Object` collates through the culture, and Windows PowerShell 5.1
    /// runs on .NET Framework, which uses NLS. .NET 10 uses ICU. The two
    /// disagree on exactly the characters these lists are made of: measured, NLS
    /// orders `%s :user {{a}} {0} {b.c}` and ICU orders the same list with `%s`
    /// last, and NLS puts `--ab` before `--a-flag` where ordinal does the
    /// reverse. Reproducing NLS from .NET 10 would mean forcing the whole
    /// application onto NLS for one sort.
    ///
    /// So the port sorts ordinally, which is stable, locale-free and identical
    /// on every machine. That is worth more here than byte-matching a list
    /// order, because the sort exists only to compare two bags of strings for
    /// equality -- both sides are sorted the same way and compared pairwise, so
    /// any consistent total order yields the same verdict. The verdicts are what
    /// callers act on, and they are asserted against the oracle word for word.
    /// </summary>
    internal static readonly Comparison<string> Order =
        static (a, b) => string.CompareOrdinal(a, b);
}
