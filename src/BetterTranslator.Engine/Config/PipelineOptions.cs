namespace BetterTranslator.Engine.Config;

public static class PipelineOptions
{
    public static bool PreserveByteOrderMark { get; set; } = true;

    public static bool TranslateFrontMatterProse { get; set; } = true;

    public static bool RestoreAsciiPunctuation { get; set; } = true;

    public static bool TrimIntroducedTrailingBlanks { get; set; } = true;

    public static bool DropInventedMarkup { get; set; } = true;

    public static bool EscalateRetryDecoding { get; set; } = true;

    public static bool PreserveRunBoundaries { get; set; } = true;

    public static bool RepairEmphasisRuns { get; set; } = true;

    public static bool FlagUnverifiedUnits { get; set; } = true;

    public static bool ProtectQuotedCitations { get; set; } = true;

    public static bool RecoverEchoedUnits { get; set; } = true;
}
