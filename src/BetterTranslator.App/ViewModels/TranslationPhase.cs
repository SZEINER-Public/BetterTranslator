namespace BetterTranslator.App.ViewModels;

/// <summary>
/// Where one entry stands with its translation. The result region renders from
/// this and from the response text, and from nothing else: the source has no
/// route into it in any of these states, which is the whole reason the phase is
/// modelled rather than inferred from whether some string happens to be empty.
///
/// Distinct from <see cref="Core.Models.EntryState"/>, which is what the
/// database holds. This one covers the in-flight window, which is not a stored
/// fact.
/// </summary>
public enum TranslationPhase
{
    /// <summary>
    /// Nothing has been asked for this entry and nothing came back for it. The
    /// result region renders nothing at all -- not the source, and not an empty
    /// heading over a blank line pretending to be an answer.
    /// </summary>
    Idle,

    /// <summary>
    /// A request is out and no output has arrived. The placeholder covers this,
    /// once it has been owed its delay.
    /// </summary>
    Pending,

    /// <summary>
    /// Output is arriving in pieces. Unreachable today and deliberately kept:
    /// the runtime hands back a whole <see cref="Runtime.Inference.TranslationOutcome"/>
    /// with no incremental surface, and every gate in the engine judges the
    /// answer as a whole, so there is nothing partial that would be safe to put
    /// on screen. When a streaming boundary exists this is the state it enters.
    /// </summary>
    Streaming,

    /// <summary>The answer is in and is what the region renders.</summary>
    Complete,

    /// <summary>
    /// Nothing came back, or a gate refused what did. The region says so and
    /// offers Retry. It never falls back to the source.
    /// </summary>
    Failed,
}
