using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Engine.Verification.Structure;

public sealed record MaskTrace(string Sentinel, string Original, ExemptionReason Reason);

public sealed record SegmentTrace(
    int SourceStart,
    int SourceLength,
    SegmentOutcome Outcome,
    string? Answer = null,
    IReadOnlyList<MaskTrace>? Masks = null,
    string? Spliced = null,
    int? TargetStart = null,
    int? TargetLength = null)
{
    public IReadOnlyList<MaskTrace> MaskList => Masks ?? [];

    public static ExemptionReason ReasonFor(string original)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || original.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ExemptionReason.Url;
        }

        if (original.StartsWith('`') || original.StartsWith("```", StringComparison.Ordinal))
        {
            return ExemptionReason.CodeSpan;
        }

        if (original.StartsWith('{') || original.StartsWith('%') || original.StartsWith('<'))
        {
            return ExemptionReason.FormatPlaceholder;
        }

        if (original.StartsWith('"') && original.EndsWith('"') && original.Length > 1)
        {
            return ExemptionReason.ProtectedName;
        }

        return ExemptionReason.MaskPlaceholder;
    }
}
