namespace BetterTranslator.Core.Verification.Checks;

public static class CheckId
{
    public static class Structure
    {
        public const string Category = "STR";

        public const string ChunkParity = "STR-101";

        public const string JsonParity = "STR-102";

        public const string SubtitleParity = "STR-103";

        public const string MarkdownParity = "STR-104";

        public const string TableParity = "STR-105";

        public const string PlaceholderCensus = "STR-106";

        public const string PlaceholderDefect = "STR-107";

        public const string BoundaryIntegrity = "STR-108";

        public const string InvariantMultiset = "STR-109";
    }

    public static class Coverage
    {
        public const string Category = "COV";

        public const string CopyThrough = "COV-101";

        public const string EmptyOutput = "COV-102";

        public const string DroppedUnit = "COV-103";

        public const string SourceTokenSurvival = "COV-104";

        public const string SegmentLanguage = "COV-105";

        public const string ExemptionFilter = "COV-106";
    }
}
