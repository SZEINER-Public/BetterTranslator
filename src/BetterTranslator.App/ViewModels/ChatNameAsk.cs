namespace BetterTranslator.App.ViewModels;

/// <summary>
/// A request for the model to name a chat, in the shape the shell can answer.
///
/// It mirrors <see cref="TranslationAsk"/> deliberately: the workspace knows
/// what was said and which language it was said into, and the shell knows which
/// model is resident and where it lives.
/// </summary>
/// <param name="Provisional">
/// The truncated name the chat is carrying. It is the fallback the shell
/// translates when the model will not summarise, so it travels with the ask.
/// </param>
public sealed record ChatNameAsk(
    string Source,
    Core.Languages.TranslationDirection Direction,
    string Provisional);
