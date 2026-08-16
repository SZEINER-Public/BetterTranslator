using BetterTranslator.Engine.Chats;

namespace BetterTranslator.Engine.Models;

/// <summary>
/// The prompt that asks the loaded model to name a chat.
///
/// It is a sibling of <see cref="TranslationPromptBuilder"/> rather than a mode
/// of it, and it deliberately does not consult <see cref="ModelPrompts"/>: that
/// lookup returns the model's trained TRANSLATE instruction for any model that
/// declares one, which would replace the title request with a request to
/// translate. The chat template is the only thing shared, because turn markers
/// are the model's, not the task's.
/// </summary>
public static class TitlePromptBuilder
{
    /// <summary>
    /// Null when the model declares a template this application does not know.
    /// The translation path can fall back to the runtime's own prompt there; a
    /// title has no such fallback, and inventing markers a model was not trained
    /// on produces an answer that is worse than no title.
    /// </summary>
    public static string? Build(string message, string targetLanguage, ChatTemplate? template)
    {
        if (template is null || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return template.Render(
        [
            new ChatMessage("system", ChatTitle.Instruction(targetLanguage)),
            new ChatMessage("user", message),
        ]);
    }
}
