using System.Text.Json.Serialization;
using BetterTranslator.Core.Languages;

namespace BetterTranslator.Engine.Languages;

public sealed record TranslateTextResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("source")]
    public required string SourceCode { get; init; }

    [JsonPropertyName("target")]
    public required string TargetCode { get; init; }

    [JsonPropertyName("language")]
    public string LanguageName { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string ModelName { get; init; } = string.Empty;

    [JsonIgnore]
    public bool CanTranslate => string.Equals(Status, Ready, StringComparison.Ordinal);

    public const string Ready = "ready";
}

public static class TranslationTools
{
    public const string TranslateText = "translate_text";

    public static TranslateTextResult Inspect(
        TranslationDirection direction,
        string? modelId = null,
        string modelName = "") =>
        Inspect(new DirectionGuard(), direction, modelId, modelName);

    public static TranslateTextResult Inspect(
        DirectionGuard guard,
        TranslationDirection direction,
        string? modelId = null,
        string modelName = "")
    {
        var verdict = guard.Inspect(direction, modelId, modelName);

        return new TranslateTextResult
        {
            Status = StatusOf(verdict.Status),
            SourceCode = direction.Source.Code.Value,
            TargetCode = direction.Target.Code.Value,
            LanguageName = verdict.LanguageName,
            ModelName = verdict.ModelName,
        };
    }

    private static string StatusOf(DirectionStatus status) => status switch
    {
        DirectionStatus.Ready => TranslateTextResult.Ready,
        DirectionStatus.SameLanguage => "same_language",
        DirectionStatus.UnknownSource => "unknown_source",
        DirectionStatus.UnknownTarget => "unknown_target",
        DirectionStatus.SourceUnsupported => "source_unsupported",
        DirectionStatus.TargetUnsupported => "target_unsupported",
        _ => "unknown",
    };
}
