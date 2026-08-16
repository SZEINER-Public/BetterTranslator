using BetterTranslator.Indexing.Retrieval;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One run of a result: either plain text, or a term memory decided. The
/// underline is dotted below the unsure threshold and solid at or above it,
/// decided here rather than in markup.
/// </summary>
public sealed class MemorySegmentViewModel(string text, MemoryMatch? match, int unsureThreshold)
{
    public string Text { get; } = text;

    public MemoryMatch? Match { get; } = match;

    public bool IsFromMemory => Match is not null;

    public MemoryUnderline Underline =>
        Match?.UnderlineFor(unsureThreshold) ?? MemoryUnderline.None;

    public bool IsSolid => Underline == MemoryUnderline.Solid;

    public bool IsDotted => Underline == MemoryUnderline.Dotted;

    /// <summary>"88% confident", plain text rather than a bar alone.</summary>
    public string? ConfidenceLabel => Match?.ConfidenceLabel;

    public string? Origin => Match?.Origin;

    public string? Reason => Match?.Reason;

    public IReadOnlyList<string> Alternatives => Match?.Alternatives ?? [];

    public bool HasAlternatives => Alternatives.Count > 0;

    /// <summary>Stated on hover, so a mark is never unexplained.</summary>
    public string? Tooltip => Match is null
        ? null
        : $"{Match.Origin} - {Match.ConfidenceLabel}";
}
