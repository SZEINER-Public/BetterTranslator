namespace BetterTranslator.Core.Languages;

public enum DirectionStatus
{
    Ready,
    UnknownSource,
    UnknownTarget,
    SameLanguage,
    SourceUnsupported,
    TargetUnsupported,
}

public sealed record DirectionVerdict(DirectionStatus Status)
{
    public static DirectionVerdict Ready { get; } = new(DirectionStatus.Ready);

    public string LanguageName { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public bool CanSend => Status == DirectionStatus.Ready;

    public bool IsSameLanguage => Status == DirectionStatus.SameLanguage;
}
