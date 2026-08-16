using System.Globalization;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// What the runtime panel says when the sampler that went out is not the one the
/// reader asked for.
///
/// A model whose card publishes a greedy example has its whole sampler written
/// over by <see cref="Runtime.Verification.SamplerConfigGuard"/>, temperature
/// included. That is deliberate, and it was also silent: the panel handed the
/// guard's own output back to the guard's own rules, which of course conformed,
/// so the one thing worth saying could never be said.
///
/// A function of two numbers and a name, so the sentence can be tested without a
/// window.
/// </summary>
public static class SamplerAdvisory
{
    /// <summary>
    /// Half of the slider's own step, so a value the reader cannot express is
    /// never reported as a difference.
    /// </summary>
    private const double Same = 0.005;

    public static string? Describe(string modelName, double requested, double sent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Invariant, like the readout beside the slider and for the same reason:
        // these are engine parameters quoted back to whoever set them, and a
        // sentence that says 0,70 about a control showing 0.70 reads as a
        // different number.
        return Math.Abs(requested - sent) < Same
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Temperature {requested:0.00} was not sent: {modelName} runs greedy, so {sent:0.00} went out.");
    }
}
