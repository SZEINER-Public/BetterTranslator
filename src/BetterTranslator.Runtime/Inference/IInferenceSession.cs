namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// A loaded model, wherever it lives.
///
/// The whole reason this is an interface: the native runtime can call
/// <c>abort()</c>. Measured, with a stack from the dump -- an assert deep inside
/// <c>br_gen_start</c> raised <c>FAST_FAIL_FATAL_APP_EXIT</c>, and a fast-fail
/// walks past every managed handler there is. In-process that ends the window
/// mid-sentence with no dialog and no log. In a child process it ends a process
/// that was doing one thing, and the translation reports a refusal like any
/// other.
/// </summary>
/// <summary>
/// What one generation produced, or why it produced nothing.
///
/// A result rather than an exception, and that is the point: the session this
/// travels through exists BECAUSE the model can die mid-generation, so the death
/// is an expected outcome and not an exceptional one. Throwing it from inside a
/// <c>Task.Run</c> also means no user frame handles it at the moment it is
/// raised -- the awaiting catch runs later, on resumption -- so a debugger
/// stops on every one of them and calls it unhandled.
/// </summary>
public readonly record struct Completion(string Text, int Tokens, string? Fault)
{
    public static Completion Failed(string fault) => new(string.Empty, 0, fault);

    public bool Faulted => Fault is not null;
}

public interface IInferenceSession : IDisposable
{
    /// <summary>False once the session has died and has to be replaced.</summary>
    bool IsAlive { get; }

    Completion CompleteCounted(string prompt, GenParams sampling, CancellationToken cancellationToken);

    /// <summary>The prompt the runtime would build for a translation, without running it.</summary>
    string BuildTranslatePrompt(string text, Language from, Language to);
}

/// <summary>
/// The model in this process, which is what every version before the host
/// existed did. Kept as the fallback: a machine where the child cannot be
/// started should still translate.
/// </summary>
public sealed class InProcessSession(BetterRuntimeModel model) : IInferenceSession
{
    private bool _disposed;

    public bool IsAlive => !_disposed;

    /// <summary>
    /// In this process a fatal runtime fault never returns at all, so there is
    /// no death to report: anything that does come back is either an answer or a
    /// real runtime error, and a real error is still worth raising.
    /// </summary>
    public Completion CompleteCounted(string prompt, GenParams sampling, CancellationToken cancellationToken)
    {
        var (text, tokens) = model.CompleteCounted(prompt, sampling, cancellationToken);

        return new Completion(text, tokens, null);
    }

    public string BuildTranslatePrompt(string text, Language from, Language to) =>
        model.BuildTranslatePrompt(text, from, to);

    public void Dispose()
    {
        _disposed = true;
        model.Dispose();
    }
}
