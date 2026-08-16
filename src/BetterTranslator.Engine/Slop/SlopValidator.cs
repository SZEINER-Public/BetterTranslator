using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Slop;

/// <summary>What the validator decided.</summary>
public enum SlopVerdict
{
    /// <summary>Accepted, possibly after symbol or content repairs.</summary>
    Ok,

    /// <summary>A content-tier problem was repaired; the caller may retry once.</summary>
    Content,

    /// <summary>A placeholder changed. The text is the SOURCE, unmodified.</summary>
    Integrity,
}

/// <summary>The text to use, the verdict, and what was done to get there.</summary>
public sealed record SlopResult(SlopVerdict Verdict, string Text, IReadOnlyList<string> Applied);

/// <summary>Tier 1: symbols the model reaches for that the house style does not use.</summary>
public sealed record SymbolRules
{
    public bool EmDashToHyphen { get; init; } = true;

    public bool NormaliseNbsp { get; init; } = true;

    /// <summary>"cs" pairs low-opening with high-closing quotes; "none" leaves them.</summary>
    public string QuoteStyle { get; init; } = "none";

    /// <summary>
    /// Only rewrite when the source had none. A deliberate em dash in the source
    /// is not slop, and rewriting it would be the tool corrupting correct input.
    /// </summary>
    public bool OnlyIfAbsentInSource { get; init; } = true;
}

/// <summary>Tier 2: wording that is about the task rather than part of it.</summary>
public sealed record ContentRules
{
    public bool StripLeadingPreamble { get; init; } = true;

    public IReadOnlyList<string> BannedPhrases { get; init; } = [];

    public IReadOnlyList<string> BannedWords { get; init; } = [];
}

/// <summary>Tier 3: what must come back unchanged or the source is kept.</summary>
public sealed record IntegrityRules
{
    public IReadOnlyList<string> Placeholders { get; init; } = [];
}

public sealed record SlopConfig
{
    public string Language { get; init; } = "cs";

    public SymbolRules Symbols { get; init; } = new();

    public ContentRules Content { get; init; } = new();

    public IntegrityRules Integrity { get; init; } = new();

    /// <summary>
    /// The shipped defaults, matching `Get-DefaultSlopConfig`. Czech gets the
    /// quote conversion; every other language gets none, because the pairing is
    /// a property of the target language rather than of the model.
    /// </summary>
    public static SlopConfig Default(string language = "cs") => new()
    {
        Language = language,
        Symbols = new SymbolRules
        {
            EmDashToHyphen = true,
            NormaliseNbsp = true,
            QuoteStyle = language == "cs" ? "cs" : "none",
            OnlyIfAbsentInSource = true,
        },
        Content = new ContentRules
        {
            StripLeadingPreamble = true,
            BannedPhrases =
            [
                "Here is the translation",
                "Here is the translated",
                "Zde je preklad",
                "Zde je překlad",
                "Of course",
                "Certainly",
                "Sure,",
                "As an AI",
            ],
            BannedWords = [],
        },
        Integrity = new IntegrityRules
        {
            // The colon form excludes a preceding colon, word character or
            // dollar. Without that it matched '::WriteAllText' and
            // '-Confirm:$false' in technical prose and invented placeholders
            // that could never match.
            Placeholders =
            [
                @"\{\{\s*[^}]+\s*\}\}",
                @"\{[A-Za-z0-9_.]+\}",
                @"\{[0-9]+\}",
                @"(?<![A-Za-z0-9_:$]):[A-Za-z_][A-Za-z0-9_]*",
                "%[sdfx]",
                @"\[\[\d+\]\]",
            ],
        },
    };
}

/// <summary>
/// The slop validator, ported from `rag.ps1`.
///
/// Three tiers, driven entirely by configuration. The validator is a plain
/// script by design: no model is consulted about the quality of another model's
/// output, because the thing that produced the error is the last thing that
/// should be asked to judge it. That is also what makes every tier testable
/// without a server.
/// </summary>
public static class SlopValidator
{
    private const char NoBreakSpace = ' ';
    private const char CzechOpenQuote = '„';
    private const char CzechCloseQuote = '“';

    private static readonly Regex EmDashRun = new(@"\s*—\s*", RegexOptions.Compiled);
    private static readonly Regex BalancedQuotes = new("\"([^\"\r\n]+)\"", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static SlopResult Validate(string source, string? translated, SlopConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        config ??= SlopConfig.Default();
        var applied = new List<string>();

        if (string.IsNullOrWhiteSpace(translated))
        {
            return new SlopResult(SlopVerdict.Integrity, source, ["integrity: empty answer, source kept"]);
        }

        var symbols = RepairSymbols(source, translated!, config.Symbols);
        applied.AddRange(symbols.Applied);

        var content = RepairContent(symbols.Text, config.Content);
        applied.AddRange(content.Applied);

        var before = Placeholders(source, config.Integrity.Placeholders);
        var after = Placeholders(content.Text, config.Integrity.Placeholders);

        if (!before.SequenceEqual(after, StringComparer.Ordinal))
        {
            applied.Add(
                $"integrity: placeholders changed, expected [{string.Join(",", before)}] "
                + $"got [{string.Join(",", after)}] - source kept");

            return new SlopResult(SlopVerdict.Integrity, source, applied);
        }

        return new SlopResult(
            content.Applied.Count > 0 ? SlopVerdict.Content : SlopVerdict.Ok,
            content.Text,
            applied);
    }

    /// <summary>Tier 1. Returns the rewritten text and what was rewritten.</summary>
    public static (string Text, IReadOnlyList<string> Applied) RepairSymbols(
        string source, string translated, SymbolRules rules)
    {
        var applied = new List<string>();
        var text = translated;
        var onlyIfAbsent = rules.OnlyIfAbsentInSource;

        if (rules.EmDashToHyphen
            && text.Contains('—', StringComparison.Ordinal)
            && (!onlyIfAbsent || !source.Contains('—', StringComparison.Ordinal)))
        {
            text = EmDashRun.Replace(text, " - ");
            applied.Add("symbols: em dash to hyphen");
        }

        if (rules.NormaliseNbsp
            && text.Contains(NoBreakSpace)
            && (!onlyIfAbsent || !source.Contains(NoBreakSpace)))
        {
            text = text.Replace(NoBreakSpace, ' ');
            applied.Add("symbols: non-breaking space normalised");
        }

        // Only balanced pairs are converted; an apostrophe or a lone quote is
        // left alone.
        if (string.Equals(rules.QuoteStyle, "cs", StringComparison.Ordinal) && BalancedQuotes.IsMatch(text))
        {
            text = BalancedQuotes.Replace(text, $"{CzechOpenQuote}$1{CzechCloseQuote}");
            applied.Add("symbols: Czech quotation marks");
        }

        return (text, applied);
    }

    /// <summary>
    /// Tier 2. A banned phrase is stripped only as a leading preamble, anchored
    /// at the start and only up to the first colon or newline, so a phrase
    /// appearing legitimately mid-text is not cut. A banned word is reported
    /// rather than removed -- deleting a word from the middle of a sentence
    /// would leave worse text than the one it complained about.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Applied) RepairContent(string translated, ContentRules rules)
    {
        var applied = new List<string>();
        var text = translated;

        if (rules.StripLeadingPreamble)
        {
            foreach (var phrase in rules.BannedPhrases)
            {
                if (string.IsNullOrEmpty(phrase))
                {
                    continue;
                }

                var rx = @"^\s*" + Regex.Escape(phrase) + @"[^\r\n:]*:?\s*";

                if (Regex.IsMatch(text, rx))
                {
                    text = Regex.Replace(text, rx, string.Empty);
                    applied.Add($"content: stripped preamble '{phrase}'");
                    break;
                }
            }
        }

        foreach (var word in rules.BannedWords)
        {
            if (!string.IsNullOrEmpty(word) && Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b"))
            {
                applied.Add($"content: banned word '{word}' present");
            }
        }

        return (text, applied);
    }

    /// <summary>
    /// Tier 3's inventory. Each pattern's matches are blanked to the same length
    /// before the next runs, so a later pattern cannot re-match inside an
    /// earlier one's text.
    ///
    /// Ordinal order, not the reference's. `Sort-Object` collates through NLS,
    /// which .NET 10 cannot reproduce; both sides here are sorted the same way
    /// and compared as a bag, so the verdict is identical.
    /// </summary>
    public static IReadOnlyList<string> Placeholders(string text, IReadOnlyList<string> patterns)
    {
        var found = new List<string>();
        var remaining = text;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            var re = new Regex(pattern);

            foreach (Match m in re.Matches(remaining))
            {
                found.Add(Whitespace.Replace(m.Value, string.Empty));
            }

            remaining = re.Replace(remaining, m => new string(' ', m.Value.Length));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// The rules block appended to the system prompt. Empty when no rules file
    /// exists, so the prompt is unchanged from before -- phase 1 never degrades
    /// the baseline.
    /// </summary>
    public static string SystemSuffix(string? rulesText) =>
        string.IsNullOrEmpty(rulesText)
            ? string.Empty
            : "\n\nTarget-language rules, follow them exactly:\n" + rulesText;
}
