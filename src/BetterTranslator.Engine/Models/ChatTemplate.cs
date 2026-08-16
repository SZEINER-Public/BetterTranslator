namespace BetterTranslator.Engine.Models;

/// <summary>One turn handed to a model.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Renders messages into the turn markers a model was trained on.
///
/// Deliberately NOT a Jinja engine. `tokenizer.chat_template` is a Jinja2
/// program, and running arbitrary Jinja to produce a prompt would be a large
/// dependency and a large attack surface for a string that is, in every model
/// this application ships, one of two shapes. So the two are recognised by the
/// markers they use and rendered directly, and anything else is declined -- the
/// caller then falls back to the runtime's own prompt rather than to a guess.
///
/// Both shapes, read from the real files on 2026-08-02:
///
///   ChatML    &lt;|im_start|&gt;{role}\n{content}&lt;|im_end|&gt;\n ... &lt;|im_start|&gt;assistant\n
///   Gemma     &lt;start_of_turn&gt;{role}\n{content}&lt;end_of_turn&gt;\n ... &lt;start_of_turn&gt;model\n
///
/// They differ in three strings and in what the assistant turn is called, which
/// is the whole of the difference this needs to carry.
/// </summary>
public sealed record ChatTemplate(
    string Name,
    string Open,
    string Close,
    string AssistantRole,
    bool SupportsSystemRole)
{
    public static ChatTemplate ChatMl { get; } =
        new("ChatML", "<|im_start|>", "<|im_end|>", "assistant", SupportsSystemRole: true);

    /// <summary>
    /// Gemma has no system role. Its template routes user and system through the
    /// same branch, so a system message renders as a SECOND user turn -- a stray
    /// message before the real one rather than a system prompt. That is why
    /// model-prompts.json marks TranslateGemma `role: user`.
    /// </summary>
    public static ChatTemplate Gemma { get; } =
        new("Gemma", "<start_of_turn>", "<end_of_turn>", "model", SupportsSystemRole: false);

    /// <summary>
    /// Which shape a declared template is, or null for one this does not know.
    ///
    /// Matched on the marker rather than on the architecture: a fine-tune keeps
    /// its parent's markers while calling itself something else, and the markers
    /// are what actually has to be emitted.
    /// </summary>
    public static ChatTemplate? Detect(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (template.Contains(Gemma.Open, StringComparison.Ordinal))
        {
            return Gemma;
        }

        return template.Contains(ChatMl.Open, StringComparison.Ordinal) ? ChatMl : null;
    }

    /// <summary>The shape the model at this path declares, or null.</summary>
    public static ChatTemplate? For(string modelPath) =>
        string.IsNullOrWhiteSpace(modelPath) ? null : Detect(GgufMetadata.ChatTemplate(modelPath));

    /// <summary>
    /// Renders the turns and appends the generation prompt, which is what tells
    /// the model it is its go.
    ///
    /// A system message is folded into the following user turn where the shape
    /// has no system role, rather than emitted as a turn of its own. Emitting it
    /// anyway is exactly the stray-message defect the reference engine records.
    /// </summary>
    public string Render(IReadOnlyList<ChatMessage> messages)
    {
        var text = new System.Text.StringBuilder();
        string? pendingSystem = null;

        foreach (var message in messages)
        {
            var isSystem = string.Equals(message.Role, "system", StringComparison.Ordinal);

            if (isSystem && !SupportsSystemRole)
            {
                pendingSystem = message.Content;
                continue;
            }

            var content = pendingSystem is null
                ? message.Content
                : pendingSystem + "\n\n" + message.Content;

            pendingSystem = null;

            text.Append(Open).Append(message.Role).Append('\n')
                .Append(content)
                .Append(Close).Append('\n');
        }

        // A system message with nothing after it still has to reach the model.
        if (pendingSystem is not null)
        {
            text.Append(Open).Append("user").Append('\n')
                .Append(pendingSystem)
                .Append(Close).Append('\n');
        }

        return text.Append(Open).Append(AssistantRole).Append('\n').ToString();
    }
}
