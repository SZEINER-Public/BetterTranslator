namespace BetterTranslator.Core.Models;

/// <summary>
/// One thing memory has learned: a term pair, a correction, a rejected
/// phrasing. Confidence is a whole percentage so the figure a popover shows is
/// the figure that was stored.
/// </summary>
public sealed class MemoryEntry
{
    public required Guid Id { get; init; }

    public required string SourceTerm { get; init; }

    public required string TargetTerm { get; set; }

    /// <summary>Which bucket in the Learned rail this belongs to.</summary>
    public required string Category { get; init; }

    /// <summary>Whole percent, 0 to 100. Compared against the unsure threshold.</summary>
    public required int Confidence { get; set; }

    /// <summary>Where it came from, for example a file name or a chat title.</summary>
    public string? Origin { get; init; }

    public required DateTimeOffset LearnedAt { get; init; }
}
