using System.Text.Json.Serialization;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Languages;

/// <summary>Where the instruction goes in the conversation.</summary>
public enum PromptRole
{
    /// <summary>
    /// Merged into the user turn. Required for Gemma-family models: their chat
    /// template renders a system message as a SECOND user turn, which is not a
    /// system prompt at all -- it is a stray message before the real one.
    /// </summary>
    User,

    /// <summary>The engine's normal two-message shape.</summary>
    System,
}

/// <summary>How one model wants to be asked, as `config\model-prompts.json` carries it.</summary>
public sealed record ModelPromptEntry
{
    /// <summary>
    /// Matched case-insensitively against the served model id. First entry that
    /// matches wins, so specific names come first in the file.
    /// </summary>
    [JsonPropertyName("match")]
    public string Match { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    /// <summary>
    /// Blank lines between the instruction and the text. Two is part of the
    /// trained format for TranslateGemma, not a style choice.
    /// </summary>
    [JsonPropertyName("blankLines")]
    public int BlankLines { get; init; } = 2;

    [JsonPropertyName("template")]
    public string Template { get; init; } = string.Empty;
}

/// <summary>The resolved instruction for one translation.</summary>
public sealed record ModelPrompt(string Label, PromptRole Role, int BlankLines, string Instruction);

internal sealed record ModelPromptDocument
{
    [JsonPropertyName("models")]
    public IReadOnlyList<ModelPromptEntry> Models { get; init; } = [];
}

/// <summary>
/// The prompt shape a model was trained on. Ported from `Get-ModelPrompt` in
/// `scripts\languages.ps1`.
///
/// Only models trained on a specific shape need an entry; anything unlisted gets
/// the engine's own prompt. This is configuration rather than a default for a
/// reason the source file states plainly: a model fine-tuned for translation has
/// a prompt it saw for the whole of that training run, and asking it a different
/// way throws away the fine-tune. The wording is not invented -- it is the
/// structure the model card publishes, reproduced verbatim including its
/// punctuation.
/// </summary>
public sealed class ModelPrompts
{
    private const string ResourceName = "BetterTranslator.Engine.Data.model-prompts.json";

    private static readonly Lazy<IReadOnlyList<ModelPromptEntry>> Embedded = new(LoadEmbedded);

    private readonly IReadOnlyList<ModelPromptEntry> _entries;

    public ModelPrompts()
        : this(Embedded.Value)
    {
    }

    /// <summary>Overridable, so a test can drive entries it wrote itself.</summary>
    public ModelPrompts(IReadOnlyList<ModelPromptEntry> entries) => _entries = entries;

    public IReadOnlyList<ModelPromptEntry> All => _entries;

    /// <summary>
    /// Read through the config store, so an edited file is the one that loads.
    /// An empty list is the documented answer for "no model needs a special
    /// shape", so it is also the safe fallback.
    /// </summary>
    private static IReadOnlyList<ModelPromptEntry> LoadEmbedded() =>
        BomSafeJson.Deserialize<ModelPromptDocument>(Config.ConfigStore.BytesFor("model-prompts"))?.Models ?? [];

    /// <summary>
    /// The instruction this model was trained on, or null for "ask it normally".
    ///
    /// Null is not a failure. It is the documented answer for every model the
    /// file does not list, and the caller is expected to fall back to the
    /// engine's own prompt rather than to invent one.
    /// </summary>
    public ModelPrompt? For(
        string? modelId,
        string sourceLanguage = "English",
        string sourceCode = "en",
        string targetLanguage = "",
        string targetCode = "")
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        foreach (var entry in _entries)
        {
            // Matched by identity rather than by substring. The id reaching here
            // is usually a file path, and the same model is written
            // "gemma-3-4b" by its publisher and "gemma3-4b" by whatever
            // downloaded it -- a substring test then finds no entry, the model
            // silently loses its trained prompt shape, and the only symptom is
            // worse output.
            if (entry.Match.Length == 0 || !Models.ModelIdentity.Matches(modelId, entry.Match))
            {
                continue;
            }

            var text = entry.Template
                .Replace("{SOURCE_CODE}", sourceCode, StringComparison.Ordinal)
                .Replace("{TARGET_CODE}", targetCode, StringComparison.Ordinal)
                .Replace("{SOURCE}", sourceLanguage, StringComparison.Ordinal)
                .Replace("{TARGET}", targetLanguage, StringComparison.Ordinal);

            return new ModelPrompt(
                entry.Label,
                string.Equals(entry.Role, "system", StringComparison.OrdinalIgnoreCase)
                    ? PromptRole.System
                    : PromptRole.User,
                entry.BlankLines,
                text);
        }

        return null;
    }

    /// <summary>
    /// True when this model publishes a trained prompt shape. The distinction
    /// the caller needs: an unlisted model is asked in a way it was never tuned
    /// for, and that is worth saying out loud rather than discovering in the
    /// quality of the output.
    /// </summary>
    public bool HasTrainedShape(string? modelId) => For(modelId) is not null;
}
