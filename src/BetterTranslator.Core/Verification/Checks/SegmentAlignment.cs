namespace BetterTranslator.Core.Verification.Checks;

public enum SegmentOutcome
{
    Translated,
    Recovered,
    Kept,
    Dropped,
    Stopped,
}

public sealed record MaskRecord(
    int Ordinal,
    string Sentinel,
    string Original,
    ExemptionReason Reason,
    CheckRange SourceRange,
    CheckRange? TargetRange,
    int ReturnedCount,
    bool ReturnedIntact,
    bool SourceLocated,
    DelimiterClass SourceLeft,
    DelimiterClass SourceRight,
    DelimiterClass TargetLeft,
    DelimiterClass TargetRight);

public sealed record SegmentAlignment(
    string Identity,
    CheckRange SourceRange,
    CheckRange? TargetRange,
    SegmentOutcome Outcome,
    IReadOnlyList<MaskRecord> Masks);
