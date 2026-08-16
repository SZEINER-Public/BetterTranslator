using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Json;

/// <summary>
/// Lifts out everything inside a resource string that is machinery rather than
/// language, and puts it back afterwards.
///
/// A value in an i18n file is rarely just a sentence. `Deleted {count} of
/// {total} files` has two slots the runtime fills; `Press <b>Save</b>` has
/// markup; `Line one\nLine two` has a break that means something. A model shown
/// those will translate `count`, drop a tag, or turn `%s` into `%p`, and the
/// application then formats a string against arguments that no longer match --
/// which is a crash at the call site, not a bad translation.
///
/// So they never reach it. The sentinel form is the one measured in
/// <see cref="Markup.MarkupGuard"/>: brackets and digits, nothing wordlike.
/// </summary>
public static class JsonValueGuard
{
    /// <summary>
    /// Order is load-bearing. The double-brace form has to be taken before the
    /// single-brace one or `{{name}}` is matched as `{` plus `{name}`, and the
    /// positional `%1$s` before the bare `%s` for the same reason.
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        // {{ mustache }} and {{name}}
        new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled),

        // ICU and .NET: {0}, {name}, {0:C}, {count, plural, one {#} other {#}}.
        // One level of nesting is allowed inside, which covers ICU plurals
        // without trying to be a parser.
        new(@"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Compiled),

        // printf, positional first
        new(@"%\d+\$[sdifxX]", RegexOptions.Compiled),
        new(@"%[sdifxX]", RegexOptions.Compiled),

        // An HTML-ish tag, opening, closing or self-closing
        new(@"</?[A-Za-z][A-Za-z0-9]*(?:\s[^<>]*)?/?>", RegexOptions.Compiled),

        // A URL. Never translatable, and a mangled one is a dead link. Taken
        // before the colour pattern, or the fragment of
        // `https://example.com/#abc123` would be lifted out of the middle of it.
        new(@"https?://[^\s""]+", RegexOptions.Compiled),

        // A hex colour. `#FF0000` is six letters and digits to anything asking
        // "is there language here", which is how a palette entry ends up being
        // sent to a translation model.
        new(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled),

        // Control characters the document wrote as escapes and that decoding
        // turned back into real characters. A newline inside a value is layout
        // and has to come back in the same place.
        new(@"[\r\n\t]", RegexOptions.Compiled),
    ];

    private static readonly Regex Sentinel = new(@"\[\[(\d+)\]\]", RegexOptions.Compiled);

    /// <summary>
    /// Replaces machinery with sentinels numbered from <paramref name="first"/>,
    /// so a batch can number every value's guards in one sequence and no two
    /// collide.
    /// </summary>
    public static (string Text, IReadOnlyList<JsonGuard> Guards) Protect(string value, int first = 0)
    {
        ArgumentNullException.ThrowIfNull(value);

        var guards = new List<JsonGuard>();
        var text = value;

        foreach (var pattern in Patterns)
        {
            text = pattern.Replace(text, match =>
            {
                var sentinel = $"[[{first + guards.Count}]]";
                guards.Add(new JsonGuard(sentinel, match.Value));
                return sentinel;
            });
        }

        return (text, guards);
    }

    /// <summary>Puts the machinery back. Highest index first, so `[[1]]` does not match inside `[[10]]`.</summary>
    public static string Restore(string text, IReadOnlyList<JsonGuard> guards)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guards);

        var restored = text;

        for (var i = guards.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace(guards[i].Sentinel, guards[i].Original, StringComparison.Ordinal);
        }

        return restored;
    }

    /// <summary>
    /// True when the answer carried every sentinel through, once each and in
    /// order, and invented none. Order matters as much as presence: two slots
    /// swapped is a sentence that formats the wrong argument into the wrong
    /// place, which reads as correct and is not.
    /// </summary>
    public static bool Holds(string? answer, IReadOnlyList<JsonGuard> guards)
    {
        ArgumentNullException.ThrowIfNull(guards);

        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var found = Sentinel.Matches(answer);

        if (found.Count != guards.Count)
        {
            return false;
        }

        for (var i = 0; i < guards.Count; i++)
        {
            if (found[i].Value != guards[i].Sentinel)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when there is language in here at all. A value that is nothing but a
    /// slot, a number or a colour has nothing to translate, and sending it costs
    /// a call to be told so.
    /// </summary>
    public static bool WorthSending(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var (stripped, _) = Protect(value);
        return stripped.Any(char.IsLetter);
    }
}
