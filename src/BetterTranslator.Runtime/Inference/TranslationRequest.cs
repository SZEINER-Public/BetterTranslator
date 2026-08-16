using BetterTranslator.Core.Models;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// One send, as the caller knows it: the text, the pair, and whether this
/// project's knowledge was asked for. Everything else about the job -- sampling,
/// the standing instruction, which vocabularies apply -- is settled from the
/// stored settings by <see cref="TranslationJobs"/>, in one place, for every
/// surface.
/// </summary>
/// <param name="UsesMemory">
/// Whether this project's knowledge was asked for: the composer's Memory chip in
/// the window, and the memory argument on the agent surfaces. Not the same
/// question as whether retrieval found anything -- an unindexed project answers
/// nothing and the glossary still applies -- which is why this travels beside
/// <paramref name="Memory"/> rather than being inferred from it.
/// </param>
/// <param name="IsStandalone">
/// This text is a unit a person typed rather than a chunk of a document. See
/// <see cref="TranslationJob.IsStandalone"/> for the two gates it relaxes.
/// </param>
public sealed record TranslationRequest(
    string Text,
    Core.Languages.TranslationDirection Direction,
    string ModelPath,
    string? Memory = null,
    bool UsesMemory = false,
    bool IsStandalone = true);

/// <summary>
/// The one place a <see cref="TranslationJob"/> is built.
///
/// It exists because the alternative was measured: three call sites filled the
/// record by hand, and the two outside the chat quietly left out the standing
/// instruction, the temperature, the retrieved memory and the project glossary.
/// Nothing about those omissions was visible -- every field has a sensible
/// default -- so the window and the agent surfaces sent different prompts for
/// the same text and the same settings, and went on doing it.
///
/// A caller now says what it knows and cannot decline to answer the rest.
/// </summary>
public static class TranslationJobs
{
    public static TranslationJob For(TranslationRequest request, AppSettings settings) => new()
    {
        Text = request.Text,
        ModelPath = request.ModelPath,
        Direction = request.Direction,
        Effort = settings.Effort,
        Temperature = (float)settings.Temperature,
        Instruction = string.IsNullOrWhiteSpace(settings.Instruction) ? null : settings.Instruction,
        Memory = request.Memory,

        // The one switch. Everything that brings this project's knowledge into a
        // send is behind it -- the chat's earlier pairs, the passages retrieval
        // found, and the project's own glossary -- so that a send without it
        // sees the text and nothing else.
        UseProjectVocabulary = request.UsesMemory,

        IsStandalone = request.IsStandalone,
        UseDomainVocabulary = settings.UseDomainVocabulary,
    };
}
