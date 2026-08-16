using BetterTranslator.Engine.Languages;

namespace BetterTranslator.Engine.Models;

/// <summary>A prompt that was built, and what went into it.</summary>
public sealed record BuiltPrompt(string Text, string TemplateName, string InstructionSource);

/// <summary>
/// Builds the whole prompt for one translation: the model's own turn markers,
/// the instruction that model was trained on where it has one, and the engine's
/// own where it has not.
///
/// This exists because the runtime builds exactly one prompt -- TranslateGemma's
/// instruction inside Gemma markers -- for every model it loads. That is right
/// for TranslateGemma and wrong twice over for anything else. The two facts
/// needed to do better are both available: the model's template is in its own
/// GGUF header, and which instruction it wants is in model-prompts.json.
///
/// Returns null rather than guessing when the template is one it does not know.
/// The caller then uses the runtime's prompt, which is what happened before.
/// </summary>
public static class TranslationPromptBuilder
{
    /// <summary>
    /// Builds, or returns null when the model declares no template this can
    /// render.
    /// </summary>
    /// <param name="preamble">
    /// Retrieved memory and any standing instruction, already assembled. Goes
    /// ahead of the translator instruction, never between it and the text --
    /// anything after that instruction reads as the text to translate.
    /// </param>
    public static BuiltPrompt? Build(
        string modelPath,
        string text,
        string sourceLanguage,
        string sourceCode,
        string targetLanguage,
        string targetCode,
        string? preamble = null,
        string? rulesText = null,
        ChatTemplate? templateOverride = null,
        ModelPrompts? prompts = null,
        PromptFragments? fragments = null)
    {
        var template = templateOverride ?? ChatTemplate.For(modelPath);

        if (template is null)
        {
            return null;
        }

        var modelId = string.IsNullOrEmpty(modelPath) ? string.Empty : Path.GetFileNameWithoutExtension(modelPath);
        var trained = (prompts ?? new ModelPrompts()).For(modelId, sourceLanguage, sourceCode, targetLanguage, targetCode);
        var wording = fragments ?? PromptFragments.Current;

        var messages = new List<ChatMessage>();
        string source;

        if (trained is not null)
        {
            // The model card's own shape, reproduced: instruction, its blank
            // lines, then the text, all in one turn where the file says so.
            var separator = new string('\n', trained.BlankLines + 1);
            var body = Head(preamble) + trained.Instruction + separator + text;

            if (trained.Role == PromptRole.System)
            {
                messages.Add(new ChatMessage("system", Head(preamble) + trained.Instruction));
                messages.Add(new ChatMessage("user", text));
            }
            else
            {
                messages.Add(new ChatMessage("user", body));
            }

            source = trained.Label;
        }
        else
        {
            // No published shape, so the engine's own, with the target-language
            // rules appended exactly as Get-TranslationSystemPrompt appends
            // them. Empty when no rules file exists, so the prompt is unchanged
            // from before and the baseline never degrades.
            var system = wording.SystemPrompt(targetLanguage)
                + Slop.SlopValidator.SystemSuffix(rulesText);

            messages.Add(new ChatMessage("system", Head(preamble) + system));
            messages.Add(new ChatMessage("user", text));

            source = "engine";
        }

        return new BuiltPrompt(template.Render(messages), template.Name, source);
    }

    private static string Head(string? preamble) =>
        string.IsNullOrWhiteSpace(preamble) ? string.Empty : preamble.TrimEnd() + "\n\n";
}
