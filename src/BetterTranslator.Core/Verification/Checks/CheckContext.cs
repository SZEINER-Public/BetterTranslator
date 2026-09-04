namespace BetterTranslator.Core.Verification.Checks;

public sealed class CheckContext
{
    public required DocumentModel Source { get; init; }

    public required DocumentModel Target { get; init; }

    public IReadOnlyList<SegmentAlignment> Alignment { get; init; } = [];

    public IExemptionRegistry Exemptions { get; init; } = ExemptionRegistry.Empty;

    public required IFormatAdapter Adapter { get; init; }

    public CheckRunSettings Settings { get; init; } = new();
}
