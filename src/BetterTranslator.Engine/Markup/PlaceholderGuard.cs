using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>What was lifted out of one unit, and the text that went in its place.</summary>
public sealed record PlaceholderGuards(string Text, IReadOnlyList<string> Originals)
{
    public static readonly PlaceholderGuards None = new(string.Empty, []);

    public bool Any => Originals.Count > 0;
}

/// <summary>
/// Hides the machinery inside a line somebody typed, so the model cannot
/// translate it.
///
/// Measured, on a CLI specification pasted into the composer: `&lt;name&gt;.&lt;to&gt;.&lt;ext&gt;`
/// came back as `&lt;název&gt;.&lt;cíl&gt;.&lt;rozšíření&gt;`. Every gate passed it -- the
/// counts matched, the brackets balanced, the Czech was good -- because nothing
/// downstream knows that the words inside those brackets are not words.
///
/// The sentinel form is the one <see cref="MarkupGuard"/> measured and
/// <see cref="Json.JsonValueGuard"/> already runs in production against these
/// same models: brackets and digits, nothing wordlike.
///
/// Two rules keep this from becoming the failure that cut MarkupGuard's own list
/// down. It is capped, because a 4B model asked to echo fifty opaque tokens in
/// order echoes none of them and the whole unit is lost; and a unit whose
/// sentinels do not come back intact is retried without them, so the worst case
/// is the translation this produced before it existed rather than no translation
/// at all.
/// </summary>
public static class PlaceholderGuard
{
    /// <summary>
    /// Above this the protection is dropped rather than attempted. A chat line
    /// holds a handful of these; a count in the dozens means the text is markup
    /// with prose in it, which is the shape a model stops reproducing sentinels
    /// for.
    /// </summary>
    public const int MaxSentinels = 8;

    /// <summary>
    /// Order is load-bearing: the double-brace form has to be taken before the
    /// single-brace one, and the positional `%1$s` before the bare `%s`, or the
    /// shorter pattern matches half of the longer one.
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled),
        new(@"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Compiled),
        new(@"%\d+\$[sdifxX]", RegexOptions.Compiled),
        new(@"%[sdifxX]", RegexOptions.Compiled),

        // A CLI placeholder and an HTML-ish tag are the same shape, and the right
        // answer for both is the same: come back byte for byte. The letter has to
        // touch the bracket, so prose comparing two values -- "a < b and c > d" --
        // is not a tag.
        new(@"</?[A-Za-z][A-Za-z0-9._-]*(?:\s[^<>]*)?/?>", RegexOptions.Compiled),
    ];

    private static readonly Regex Sentinel = new(@"\[\[(\d+)\]\]", RegexOptions.Compiled);

    /// <summary>
    /// Every placeholder form in one pattern, in the same order and therefore
    /// with the same precedence: regex alternation is leftmost-first, so the
    /// double-brace arm still wins over the single-brace one.
    /// </summary>
    private static readonly Regex AnyPlaceholder = new(
        @"\{\{[^{}]*\}\}"
        + @"|\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}"
        + @"|%\d+\$[sdifxX]"
        + @"|%[sdifxX]"
        + @"|</?[A-Za-z][A-Za-z0-9._-]*(?:\s[^<>]*)?/?>",
        RegexOptions.Compiled);

    /// <summary>
    /// What a model actually returns when it does not return `[[0]]`.
    ///
    /// Measured against TranslateGemma: the sentinel survives far more often
    /// than it arrives intact. It comes back spaced, single-bracketed, in
    /// full-width brackets, or with the digit set in its own delimiters. Every
    /// one of those still says exactly which slot it is, so reading them is the
    /// difference between a placeholder that survives and a whole line that
    /// falls back.
    /// </summary>
    private static readonly Regex Drifted = new(
        @"(?:\[\s*\[\s*(\d+)\s*\]\s*\]"
        + @"|\[\s*(\d+)\s*\]"
        + @"|【\s*(\d+)\s*】"
        + @"|〔\s*(\d+)\s*〕"
        + @"|⟦\s*(\d+)\s*⟧)",
        RegexOptions.Compiled);

    /// <summary>
    /// The note that tells the model what the tokens are. It has to travel with
    /// the text rather than live in the system prompt: the trained branch sends
    /// the model card's own translate instruction and drops the engine's system
    /// prompt entirely, so on TranslateGemma the sentinels arrived with no
    /// explanation at all -- which is most of why they came back mangled.
    /// </summary>
    public const string Instruction =
        "Tokens written as [[0]], [[1]] are protected placeholders. "
        + "Copy each one into the translation exactly as it appears, once, in the same order. "
        + "Never translate them, never renumber them, and never change their brackets or spacing.";

    /// <summary>
    /// The text with its machinery replaced by sentinels, or
    /// <see cref="PlaceholderGuards.None"/> when there is nothing to protect,
    /// too much to protect, or nothing left to translate once it is protected.
    /// </summary>
    public static PlaceholderGuards Protect(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains("[[", StringComparison.Ordinal))
        {
            // Text that already carries something sentinel-shaped would have it
            // renumbered out from under whoever put it there.
            return PlaceholderGuards.None;
        }

        var originals = new List<string>();
        var output = text;

        foreach (var pattern in Patterns)
        {
            output = pattern.Replace(output, match =>
            {
                originals.Add(match.Value);
                return $"[[{originals.Count - 1}]]";
            });
        }

        if (originals.Count == 0 || originals.Count > MaxSentinels)
        {
            return PlaceholderGuards.None;
        }

        // Nothing but machinery. Sending it would spend a call to be handed the
        // sentinels back, and there was never a translation to be had.
        return Sentinel.Replace(output, " ").Any(char.IsLetter)
            ? new PlaceholderGuards(output, originals)
            : PlaceholderGuards.None;
    }

    /// <summary>
    /// One run of the source: either prose to translate, or a placeholder to
    /// carry across untouched.
    /// </summary>
    public sealed record Run(string Text, bool IsPlaceholder);

    /// <summary>
    /// The source cut at its placeholder boundaries.
    ///
    /// This is the guarantee. A sentinel depends on the model copying something;
    /// a cut depends on nothing. Translating the prose between the placeholders
    /// and putting the placeholders back where they were cannot lose one, because
    /// they were never sent anywhere. It costs word order across a placeholder --
    /// which is why it is the fallback and not the first move.
    /// </summary>
    public static IReadOnlyList<Run> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var runs = new List<Run>();
        var at = 0;

        foreach (Match match in AnyPlaceholder.Matches(text))
        {
            if (match.Index > at)
            {
                runs.Add(new Run(text[at..match.Index], false));
            }

            runs.Add(new Run(match.Value, true));
            at = match.Index + match.Length;
        }

        if (at < text.Length)
        {
            runs.Add(new Run(text[at..], false));
        }

        return runs;
    }

    /// <summary>True when this run is worth sending: it has language in it.</summary>
    public static bool IsProse(Run run) => !run.IsPlaceholder && run.Text.Any(char.IsLetter);

    /// <summary>
    /// Rewrites the sentinels a model drifted into back to the form they were
    /// sent in, so a spaced or single-bracketed one counts as reproduced.
    /// </summary>
    public static string? Normalize(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return answer;
        }

        return Drifted.Replace(answer, match =>
        {
            for (var group = 1; group < match.Groups.Count; group++)
            {
                if (match.Groups[group].Success)
                {
                    return $"[[{match.Groups[group].Value}]]";
                }
            }

            return match.Value;
        });
    }

    /// <summary>Puts the originals back. Highest index first, so `[[1]]` does not match inside `[[10]]`.</summary>
    public static string Restore(string text, PlaceholderGuards guards)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guards);

        var output = text;

        for (var i = guards.Originals.Count - 1; i >= 0; i--)
        {
            output = output.Replace($"[[{i}]]", guards.Originals[i], StringComparison.Ordinal);
        }

        return output;
    }

    /// <summary>
    /// True when the answer carried every sentinel through, once each and in
    /// order, and invented none.
    ///
    /// Order matters as much as presence. `&lt;name&gt;.&lt;to&gt;.&lt;ext&gt;`
    /// coming back as `&lt;to&gt;.&lt;name&gt;.&lt;ext&gt;` reads as correct and
    /// names a different file.
    /// </summary>
    public static bool Holds(string? answer, PlaceholderGuards guards)
    {
        ArgumentNullException.ThrowIfNull(guards);

        if (!guards.Any)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var found = Sentinel.Matches(Normalize(answer)!);

        if (found.Count != guards.Originals.Count)
        {
            return false;
        }

        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].Value != $"[[{i}]]")
            {
                return false;
            }
        }

        return true;
    }
}
