using System.Text.Json.Serialization;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Languages;

/// <summary>One piece of prompt wording the application supplies.</summary>
public sealed record PromptFragment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

internal sealed record PromptFragmentDocument
{
    [JsonPropertyName("fragments")]
    public IReadOnlyList<PromptFragment> Fragments { get; init; } = [];
}

/// <summary>
/// The application's own prompt wording, as opposed to the model's.
///
/// `model-prompts.json` carries the instruction a model was trained on, which is
/// the model card's and must not be improvised. This carries what
/// BetterTranslator adds around it -- the heading over retrieved material, and
/// the lead-in for the standing instruction from Advanced.
///
/// It is configuration rather than constants because it is prompt text: it
/// changes what the model does, and someone tuning output should be able to
/// reach it without a compiler.
/// </summary>
public sealed class PromptFragments
{
    private static readonly Lazy<IReadOnlyList<PromptFragment>> Embedded = new(Load);

    private readonly IReadOnlyList<PromptFragment> _fragments;

    public PromptFragments()
        : this(Embedded.Value)
    {
    }

    public PromptFragments(IReadOnlyList<PromptFragment> fragments) => _fragments = fragments;

    public IReadOnlyList<PromptFragment> All => _fragments;

    private static IReadOnlyList<PromptFragment> Load() =>
        BomSafeJson.Deserialize<PromptFragmentDocument>(Config.ConfigStore.BytesFor("prompts"))?.Fragments ?? [];

    /// <summary>
    /// The wording for an id, or the built-in fallback.
    ///
    /// A fallback rather than an empty string: these sit in the prompt, and a
    /// missing heading would leave retrieved passages under nothing at all --
    /// which is the arrangement measured to make the model translate them.
    /// </summary>
    public string Text(string id, string fallback) =>
        _fragments.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal))?.Text is { Length: > 0 } text
            ? text
            : fallback;

    /// <summary>
    /// The engine's own translator instruction, for a model that publishes no
    /// trained prompt of its own. Ported verbatim from
    /// `Get-TranslationSystemPrompt` in `scripts\console.ps1` and checked
    /// against it -- the wording is the reference's, not a paraphrase.
    ///
    /// The last sentence is why this matters beyond tidiness: it is the only
    /// place the model is told what a [[7]] protected block is. `MarkupGuard`
    /// lifts fences, comments, URLs and paths out and leaves those tokens in
    /// their place; a model that has never been told what they are has no reason
    /// to reproduce them, and the chunk fails its own integrity check.
    /// </summary>
    public string SystemPrompt(string targetLanguage) =>
        Text(EngineSystemId, DefaultEngineSystem)
            .Replace("{TARGET}", targetLanguage, StringComparison.Ordinal);

    public static PromptFragments Current { get; } = new();

    public const string EngineSystemId = "engineSystem";

    public const string DefaultEngineSystem =
        "You are a professional translator. Translate the user's text into {TARGET}. "
        + "Reply with the translation only: no commentary, no quotes, no explanations. "
        + "Never return the source text unchanged. "
        + "Preserve formatting, line breaks, placeholders such as {name} or %s, and any markup. "
        + "Tokens of the form [[7]] are protected blocks: reproduce each exactly once, "
        + "in the same order, and never translate or renumber them.";

    public const string MemoryHeadingId = "memoryHeading";

    public const string InstructionLeadInId = "instructionLeadIn";

    /// <summary>
    /// The defaults, kept in code as well as in the file. The file can be edited
    /// into something empty or deleted outright, and a prompt that silently lost
    /// its heading is far harder to notice than one that never had it.
    /// </summary>
    public const string DefaultMemoryHeading =
        "Here is how this project has translated related wording before. "
        + "Follow its terminology where it applies. It is reference, not the text to translate.";

    public const string DefaultInstructionLeadIn = "Also follow this instruction:";
}
