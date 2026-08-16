namespace BetterTranslator.Core.Models;

public sealed class Chat
{
    public required Guid Id { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>
    /// True while the name is still the truncated first-entry text rather than
    /// a model summary. D6 keeps it that way until a model is installed.
    /// </summary>
    public bool NameIsProvisional { get; set; } = true;
}
