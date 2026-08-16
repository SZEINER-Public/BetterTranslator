namespace BetterTranslator.Core.Languages;

public sealed record LanguageChoice(LanguageCode Code, string Name)
{
    public static LanguageChoice Unknown { get; } = new(LanguageCode.Unknown, string.Empty);

    public static LanguageChoice Of(string? code, string name, string? script = null) =>
        new(LanguageCode.From(code, script), name);

    public bool IsUnknown => Code.IsUnknown;
}

public sealed record TranslationDirection
{
    public static TranslationDirection Undecided { get; } =
        new(LanguageChoice.Unknown, LanguageChoice.Unknown);

    private TranslationDirection(LanguageChoice source, LanguageChoice target)
    {
        Source = source;
        Target = target;
    }

    public LanguageChoice Source { get; }

    public LanguageChoice Target { get; }

    public static TranslationDirection Of(LanguageChoice source, LanguageChoice target) =>
        new(source, target);

    public static TranslationDirection Between(
        string? sourceCode,
        string sourceName,
        string? targetCode,
        string targetName) =>
        new(LanguageChoice.Of(sourceCode, sourceName), LanguageChoice.Of(targetCode, targetName));

    public TranslationDirection WithSource(LanguageChoice source) => new(source, Target);

    public TranslationDirection WithTarget(LanguageChoice target) => new(Source, target);

    public TranslationDirection Swapped() => new(Target, Source);

    public bool IsSameLanguage => Source.Code.SameLanguageAs(Target.Code);

    public bool HasUnknownSource => Source.IsUnknown;

    public bool HasUnknownTarget => Target.IsUnknown;

    public bool CanSwap => !Source.IsUnknown && !Target.IsUnknown;
}
