namespace BetterTranslator.Core.Verification.Checks;

public sealed record CheckRange(string UnitPath, int Offset, int Length)
{
    public int End => Offset + Length;

    public bool Contains(CheckRange other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.Offset >= Offset && other.End <= End;
    }

    public bool Overlaps(CheckRange other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.Offset < End && Offset < other.End;
    }
}

public static class CheckCause
{
    public const string Segmentation = "segmentation";

    public const string Masking = "masking";

    public const string ModelOutput = "model-output";

    public const string Restore = "restore";

    public const string AdapterRebuild = "adapter-rebuild";
}

public sealed record CheckFinding(
    string CheckId,
    CheckRange TargetRange,
    CheckRange? SourceRange,
    CheckGranularity Granularity,
    CheckSeverity Severity,
    int Confidence,
    string CauseCode,
    string Evidence,
    CheckAction Action)
{
    public int Confidence { get; init; } = Math.Clamp(Confidence, 0, 100);
}
