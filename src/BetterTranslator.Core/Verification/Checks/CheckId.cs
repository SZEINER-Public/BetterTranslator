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

    public static class Ratio
    {
        public const string Category = "RAT";

        public const string LengthRatio = "RAT-101";

        public const string Truncation = "RAT-102";

        public const string Repetition = "RAT-103";

        public const string Compression = "RAT-104";

        public const string Insertion = "RAT-105";
    }

    public static class Runtime
    {
        public const string Category = "RUN";

        public const string SequenceConfidence = "RUN-101";

        public const string SpanLocalization = "RUN-102";

        public const string ForcedDecodeAdequacy = "RUN-103";
    }

    public static class Semantics
    {
        public const string Category = "SEM";

        public const string EmbeddingSimilarity = "SEM-101";

        public const string ReverseTranslation = "SEM-102";

        public const string ReverseComparison = "SEM-103";
    }

    public static class Terminology
    {
        public const string Category = "TRM";

        public const string AcceptedRendering = "TRM-101";

        public const string RejectedRendering = "TRM-102";

        public const string RunConsistency = "TRM-103";
    }

    public static class Gate
    {
        public const string Category = "GATE";

        public const string ExemptionFilter = "GATE-101";

        public const string RoutingPolicy = "GATE-102";

        public const string Deduplication = "GATE-103";

        public const string CandidateOrdering = "GATE-104";

        public const string Caps = "GATE-105";

        public const string RunResult = "GATE-106";
    }

    public static class Naturalness
    {
        public const string Category = "NAT";

        public const string AlignmentCrossing = "NAT-101";

        public const string TagSequenceDivergence = "NAT-102";

        public const string CliticPlacement = "NAT-103";

        public const string PronounExplicitness = "NAT-104";

        public const string NominalStyle = "NAT-105";

        public const string PassiveCalque = "NAT-106";

        public const string RegisterConsistency = "NAT-107";

        public const string SentenceBoundaryFit = "NAT-108";

        public const string TitleCaseAndQuotation = "NAT-109";

        public const string PluralCategoryCoverage = "NAT-110";
    }
}
