using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.Runtime.Agents;

public sealed record EngineAnswer(string? Text, string? Refusal, int GeneratedTokens, TimeSpan Duration)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public interface ITranslationEngine : IDisposable
{
    Task<EngineAnswer> TranslateAsync(TranslationJob job, CancellationToken cancellationToken);

    Task<EngineAnswer> TranslateDocumentAsync(
        TranslationJob job,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// A raw completion, guards and all their machinery left out.
    ///
    /// Here for one caller: naming a chat. That is not a translation -- the
    /// model is asked for a title and its answer is a label, not a rendering of
    /// anything -- so it cannot go through the translate path, and without it an
    /// agent's chat would sit in the sidebar under the truncated first sentence
    /// forever while the window's chats get named.
    /// </summary>
    Task<string?> CompleteAsync(string modelPath, string prompt, int maxTokens, CancellationToken cancellationToken);

    /// <summary>The guarded translate path as a delegate, for the namer's second tier.</summary>
    Task<TranslationOutcome> TranslateRawAsync(TranslationJob job, CancellationToken cancellationToken);
}

public sealed class LocalTranslationEngine : ITranslationEngine
{
    private readonly LocalTranslator _translator = new();

    public LocalTranslator Translator => _translator;

    public async Task<EngineAnswer> TranslateAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        var outcome = await _translator.TranslateAsync(job, cancellationToken).ConfigureAwait(false);
        var refusal = _translator.LastGuardVerdict;

        return new EngineAnswer(
            outcome.Text,
            outcome.HasText ? null : refusal ?? _translator.Reason,
            outcome.GeneratedTokens,
            outcome.Duration);
    }

    public Task<string?> CompleteAsync(
        string modelPath,
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken) =>
        _translator.CompleteAsync(modelPath, prompt, maxTokens, cancellationToken);

    public Task<TranslationOutcome> TranslateRawAsync(TranslationJob job, CancellationToken cancellationToken) =>
        _translator.TranslateAsync(job, cancellationToken);

    public async Task<EngineAnswer> TranslateDocumentAsync(
        TranslationJob job,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var document = await _translator
            .TranslateDocumentAsync(job, progress, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var refusal = _translator.LastGuardVerdict;

        return new EngineAnswer(
            document.Text,
            document.LinesTranslated > 0 ? null : refusal ?? _translator.Reason,
            document.GeneratedTokens,
            document.Duration);
    }

    public void Dispose() => _translator.Dispose();
}
