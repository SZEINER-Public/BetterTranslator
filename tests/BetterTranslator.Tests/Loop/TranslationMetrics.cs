using System.IO;

namespace BetterTranslator.Tests.Loop;

public sealed record TranslationMetrics
{
    public int SourceCharacters { get; init; }

    public int CandidateCharacters { get; init; }

    public int SourceHeadings { get; init; }

    public int SourceUnits { get; init; }

    public int CodeSpansLost { get; init; }

    public int FencedBlocksLost { get; init; }

    public int LinkTargetsLost { get; init; }

    public int PathsLost { get; init; }

    public int VersionsLost { get; init; }

    public int NumericTokensLost { get; init; }

    public int ProductNamesLost { get; init; }

    public int ProtectedTokensInvented { get; init; }

    public int UnitsMissing { get; init; }

    public int UnitsOutOfBand { get; init; }

    public int ResidualSourceSentences { get; init; }

    public int UnitsNotInTargetLanguage { get; init; }

    public int HeadingCountDelta { get; init; }

    public int HeadingLevelMismatches { get; init; }

    public int HeadingsUntranslated { get; init; }

    public int FrontMatterProseUntranslated { get; init; }

    public int ListMarkerDelta { get; init; }

    public int TableRowDelta { get; init; }

    public int ParagraphDelta { get; init; }

    public int BlankLineTopologyMismatches { get; init; }

    public int BoldMarkerDelta { get; init; }

    public int ItalicMarkerDelta { get; init; }

    public int ByteOrderMarkLost { get; init; }

    public int LineEndingChanged { get; init; }

    public int TrailingWhitespaceIntroduced { get; init; }

    public int TypographicSubstitutions { get; init; }

    public int EncodingFaults { get; init; }

    public int BoundaryFusionExcess { get; init; }

    public int SourceWordBeforeBacktick { get; init; }

    public int SourceWordAfterBacktick { get; init; }

    public int CandidateWordBeforeBacktick { get; init; }

    public int CandidateWordAfterBacktick { get; init; }

    public double TranslatedHeadingRate { get; init; }

    public double TerminologyConsistency { get; init; }

    public double MorphologicalLegality { get; init; }

    public double FluencyProxy { get; init; }

    public int RuntimeFailures { get; init; }

    public int G1ProtectedTokens =>
        CodeSpansLost + FencedBlocksLost + LinkTargetsLost + PathsLost
        + VersionsLost + NumericTokensLost + ProductNamesLost + ProtectedTokensInvented;

    public int G2Completeness => UnitsMissing + UnitsOutOfBand;

    public int G3ResidualSource => ResidualSourceSentences + UnitsNotInTargetLanguage;

    public int G4StructureParity =>
        HeadingCountDelta + HeadingLevelMismatches + HeadingsUntranslated + FrontMatterProseUntranslated
        + ListMarkerDelta + TableRowDelta + ParagraphDelta + BlankLineTopologyMismatches
        + BoldMarkerDelta + ItalicMarkerDelta;

    public int G5ByteHygiene =>
        ByteOrderMarkLost + LineEndingChanged + TrailingWhitespaceIntroduced
        + TypographicSubstitutions + EncodingFaults;

    public int G6BoundarySpacing => BoundaryFusionExcess;

    public int GateTotal => G1ProtectedTokens + G2Completeness + G3ResidualSource
        + G4StructureParity + G5ByteHygiene + G6BoundarySpacing;

    public double ScoreTotal =>
        (TranslatedHeadingRate + TerminologyConsistency + MorphologicalLegality + FluencyProxy) / 4d;

    public IReadOnlyList<(string Gate, int Count)> Gates =>
    [
        ("G1", G1ProtectedTokens),
        ("G2", G2Completeness),
        ("G3", G3ResidualSource),
        ("G4", G4StructureParity),
        ("G5", G5ByteHygiene),
        ("G6", G6BoundarySpacing),
    ];

    public IReadOnlyList<(string Label, int Count)> Defects =>
    [
        ("D1", HeadingsUntranslated),
        ("D2", FrontMatterProseUntranslated),
        ("D3", ByteOrderMarkLost),
        ("D4", ResidualSourceSentences),
        ("D5", NumericTokensLost),
        ("D6", BoundaryFusionExcess),
    ];
}

public sealed record DocumentUnderTest(string Text, bool HasByteOrderMark, string Newline)
{
    public static DocumentUnderTest FromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        return new DocumentUnderTest(text, hasBom, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    }
}
