namespace BetterTranslator.Indexing.Retrieval;

/// <summary>
/// How a word that came from memory is marked in the result.
/// </summary>
public enum MemoryUnderline
{
    /// <summary>Not from memory: no mark at all.</summary>
    None,

    /// <summary>Below the unsure threshold. Dotted, and listed under Unsure.</summary>
    Dotted,

    /// <summary>At or above the threshold. Solid.</summary>
    Solid,
}

/// <summary>
/// One term memory decided, and why. The confidence is a whole percentage so
/// the figure the popover prints is the figure that was stored, not a rounded
/// view of a float.
/// </summary>
public sealed record MemoryMatch
{
    /// <summary>The word as it appears in the result.</summary>
    public required string Term { get; init; }

    /// <summary>Where in the result it starts, so the run can be marked.</summary>
    public required int Start { get; init; }

    public int Length => Term.Length;

    /// <summary>Whole percent, 0 to 100.</summary>
    public required int Confidence { get; init; }

    /// <summary>Which bucket it came from, for example "From term pairs".</summary>
    public required string Origin { get; init; }

    /// <summary>Why memory chose it, in plain language.</summary>
    public string? Reason { get; init; }

    /// <summary>Other wordings, applied in one click.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>
    /// Dotted below the threshold, solid at or above it. The threshold is the
    /// user's setting, so this is decided per render rather than stored.
    /// </summary>
    public MemoryUnderline UnderlineFor(int unsureThresholdPercent) =>
        Confidence >= unsureThresholdPercent ? MemoryUnderline.Solid : MemoryUnderline.Dotted;

    /// <summary>True when it belongs under the Unsure filter.</summary>
    public bool IsUnsure(int unsureThresholdPercent) => Confidence < unsureThresholdPercent;

    /// <summary>"88% confident", as plain text rather than a bar alone.</summary>
    public string ConfidenceLabel => $"{Confidence}% confident";
}

/// <summary>
/// A translated result with the terms memory decided inside it.
/// </summary>
public sealed record MemoryAnnotatedResult(string Text, IReadOnlyList<MemoryMatch> Matches)
{
    public static MemoryAnnotatedResult None(string text) => new(text, []);

    public int UnsureCount(int threshold) => Matches.Count(m => m.IsUnsure(threshold));

    /// <summary>
    /// "4 terms came from memory, 1 is unsure", the line under the landing card.
    /// </summary>
    public string Summary(int threshold)
    {
        if (Matches.Count == 0)
        {
            return "Nothing came from memory";
        }

        var terms = Matches.Count == 1 ? "1 term came from memory" : $"{Matches.Count} terms came from memory";
        var unsure = UnsureCount(threshold);

        return unsure == 0 ? terms : $"{terms}, {unsure} is unsure";
    }

    /// <summary>
    /// Splits the text into runs, each either plain or carrying a match. The
    /// view renders one Run per segment, so the marking is decided here rather
    /// than in markup.
    /// </summary>
    public IReadOnlyList<(string Text, MemoryMatch? Match)> Segments()
    {
        if (Matches.Count == 0)
        {
            return [(Text, null)];
        }

        var segments = new List<(string, MemoryMatch?)>();
        var position = 0;

        foreach (var match in Matches.OrderBy(m => m.Start))
        {
            if (match.Start < position || match.Start + match.Length > Text.Length)
            {
                // Overlapping or out of range: skip rather than corrupt the text.
                continue;
            }

            if (match.Start > position)
            {
                segments.Add((Text[position..match.Start], null));
            }

            segments.Add((Text.Substring(match.Start, match.Length), match));
            position = match.Start + match.Length;
        }

        if (position < Text.Length)
        {
            segments.Add((Text[position..], null));
        }

        return segments;
    }
}
