using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Core.Verification.Gate;

public sealed record RoutedFinding(
    CheckFinding Finding,
    string Category,
    GateStage Stage,
    CheckAction Action,
    int Priority,
    IReadOnlyList<string> CheckIds,
    IReadOnlyList<string> CauseCodes)
{
    public string CheckId => Finding.CheckId;

    public CheckRange TargetRange => Finding.TargetRange;

    public CheckSeverity Severity => Finding.Severity;

    public int Confidence => Finding.Confidence;

    public CheckGranularity Granularity => Finding.Granularity;

    public bool IsDefect => Severity == CheckSeverity.Defect;

    public bool IsRepairCandidate => Action == CheckAction.Repair;

    public CheckFinding Projected => Finding with { Action = Action };

    public static RoutedFinding From(CheckFinding finding, GateStage stage, CheckAction action) =>
        new(finding, RoutingTable.CategoryOf(finding.CheckId), stage, action, 0, [finding.CheckId], [finding.CauseCode]);
}

public enum GateCheckState
{
    Ran,
    Skipped,
}

public sealed record GateCheckStatus(string Category, string CheckId, GateStage Stage, GateCheckState State, string Reason, int Findings)
{
    public bool CountsTowardPassTotal => State == GateCheckState.Ran;
}

public sealed record ScoreContribution(string CheckId, string Category, CheckRange TargetRange, int Confidence, int Priority, string Evidence);
