namespace BetterTranslator.Runtime.Agents;

public enum AgentFault
{
    None,
    UnknownLanguage,
    SameLanguage,
    ModelMissing,
    ModelDoesNotSupportLanguage,
    RuntimeUnreachable,
    InputMissing,
    OutputExists,
    UnsupportedFormat,
    TranslationFailed,
    NotFound,
    NotAvailable,
}

public sealed record AgentError(AgentFault Fault, string Message)
{
    public string Code => Fault switch
    {
        AgentFault.UnknownLanguage => "unknown_language",
        AgentFault.SameLanguage => "same_language",
        AgentFault.ModelMissing => "model_missing",
        AgentFault.ModelDoesNotSupportLanguage => "model_does_not_support_language",
        AgentFault.RuntimeUnreachable => "runtime_unreachable",
        AgentFault.InputMissing => "input_missing",
        AgentFault.OutputExists => "output_exists",
        AgentFault.UnsupportedFormat => "unsupported_format",
        AgentFault.TranslationFailed => "translation_failed",
        AgentFault.NotFound => "not_found",
        AgentFault.NotAvailable => "not_available",
        _ => "ok",
    };
}

public sealed record TextTranslation(
    string From,
    string To,
    string Model,
    string Text,
    int GeneratedTokens,
    int DurationMs,
    AgentError? Error)
{
    /// <summary>
    /// What a reader would otherwise have to notice by comparing the two texts
    /// word by word: lines that kept their source, values left untranslated,
    /// blocks retranslated phrase by phrase. Null when the run was uneventful.
    /// The window shows this under the entry; an agent gets the same sentence.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// The stored row this translation became, so the caller can reach it again:
    /// get_entry reads it, show_in_gui reveals it. Null when nothing could be
    /// stored, which is not a reason to withhold the translation.
    /// </summary>
    public string? EntryId { get; init; }

    public bool Ok => Error is null;

    public static TextTranslation Failed(string from, string to, string model, AgentError error) =>
        new(from, to, model, string.Empty, 0, 0, error);
}

public sealed record FileTranslation(
    string File,
    string Status,
    string? Out,
    string? Error,
    string? ErrorCode)
{
    /// <inheritdoc cref="TextTranslation.Note"/>
    public string? Note { get; init; }

    public static FileTranslation Done(string file, string output) => new(file, "ok", output, null, null);

    public static FileTranslation Failed(string file, AgentError error) =>
        new(file, "failed", null, error.Message, error.Code);
}

public sealed record ModelSummary(
    string Name,
    string FileName,
    string Path,
    long SizeBytes,
    bool Installed,
    bool Selected,
    string Family);

public sealed record EntrySummary(
    string Id,
    string ChatId,
    string Kind,
    string State,
    string Source,
    string Result,
    string TargetLanguage,
    string CreatedAt,
    string? FileName,
    int? GeneratedTokens,
    int? DurationMs);
