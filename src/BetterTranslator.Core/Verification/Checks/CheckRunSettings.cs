namespace BetterTranslator.Core.Verification.Checks;

public sealed class CheckRunSettings
{
    public string SourceLanguage { get; init; } = string.Empty;

    public string TargetLanguage { get; init; } = string.Empty;

    public IReadOnlySet<string> DisabledChecks { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
