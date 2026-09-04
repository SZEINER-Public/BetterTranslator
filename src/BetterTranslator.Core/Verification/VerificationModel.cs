namespace BetterTranslator.Core.Verification;

public enum SeverityTier
{
    Clean,
    Warning,
    Error,
}

public enum DefectClass
{
    None,
    MalformedForm,
    UntranslatedChunk,
    FusedToken,
}

public sealed record SignalHit(string SignalId, string FindingIds, int Penalty, string Detail);

public sealed class VerificationSpan
{
    public int Start { get; init; }

    public int Length { get; init; }

    public string Word { get; init; } = string.Empty;

    public int Score { get; set; } = 100;

    public SeverityTier Tier { get; set; } = SeverityTier.Clean;

    public DefectClass Defect { get; set; } = DefectClass.None;

    public bool Exempt { get; set; }

    public bool Unanalyzable { get; set; }

    public List<SignalHit> Signals { get; } = [];
}

public sealed record SentenceScore(int Start, int Length, int Score, SeverityTier Tier);

public sealed class VerificationResult
{
    public bool Executed { get; init; }

    public string? SkipReason { get; init; }

    public IReadOnlyList<VerificationSpan> Spans { get; init; } = [];

    public IReadOnlyList<SentenceScore> Sentences { get; init; } = [];

    public double UnanalyzableTokenRatePer1000 { get; init; }

    public Gate.GateRunResult? Gate { get; set; }

    public bool HasFindings => Executed && Spans.Any(s => !s.Exempt && s.Tier != SeverityTier.Clean);

    public static VerificationResult Skipped(string reason) => new() { Executed = false, SkipReason = reason };
}

public sealed class VerificationSettings
{
    public bool Enabled { get; set; } = true;

    public string? HunspellDicPath { get; set; }

    public string? HunspellAffPath { get; set; }

    public string? MajkaExePath { get; set; }

    public string? MajkaDictPath { get; set; }

    public string? FrequencyListPath { get; set; }

    public int WarningThreshold { get; set; } = 60;

    public int ErrorThreshold { get; set; } = 30;

    public double NgramLogProbFloor { get; set; } = -6.0;

    public long MinFrequency { get; set; } = 1;

    public int UntranslatedChunkMinRun { get; set; } = 3;

    public int PenaltyIllegalCluster { get; set; } = 45;

    public int PenaltyNgramImplausible { get; set; } = 25;

    public int PenaltyHunspellReject { get; set; } = 30;

    public int PenaltyFusedBoundary { get; set; } = 60;

    public int ConfirmedMalformedScore { get; set; } = 5;

    public int UntranslatedChunkScore { get; set; } = 10;

    public double RuntimeSignalWeight { get; set; } = 0.5;

    public Gate.GateSettings Gate { get; set; } = new();

    public RepairAutonomy Autonomy { get; set; } = RepairAutonomy.AutoRepair;

    public NaturalnessSettings Naturalness { get; set; } = new();

    public int SemanticReverseCap { get; set; } = Checks.Semantics.SemanticSettings.Default.ReverseCap;
}

public enum RepairAutonomy
{
    Off,
    AskEveryTime,
    AutoRepair,
    RepairEverything,
}

public sealed class NaturalnessSettings
{
    public bool RewriteEnabled { get; set; }

    public int CandidatesPerSentence { get; set; } = 1;

    public int RewritesPerDocument { get; set; } = 8;

    public int ImprovementMargin { get; set; } = 1;
}
