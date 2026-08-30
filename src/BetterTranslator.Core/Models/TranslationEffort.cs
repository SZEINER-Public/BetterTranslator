namespace BetterTranslator.Core.Models;

/// <summary>
/// How much the translation is allowed to cost. This is one concept, not two:
/// it picks the model that runs, and the token budget that model is given.
///
/// It lives in Core rather than beside the effort menu because three layers
/// need it -- the view model that offers it, the settings row that stores it,
/// and the runtime that turns it into generation parameters.
/// </summary>
public enum TranslationEffort
{
    /// <summary>EuroLLM. Short budget, for the common one-line translation.</summary>
    Simple,

    /// <summary>TranslateGemma. Room to work through a longer passage.</summary>
    Thinking,
}
