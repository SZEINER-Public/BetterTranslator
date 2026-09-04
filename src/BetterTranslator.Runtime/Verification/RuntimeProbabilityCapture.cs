using BetterTranslator.Core.Verification.Checks.Runtime;
using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.Runtime.Verification;

public interface IProbabilityCapture
{
    ProbabilityAvailability Availability { get; }

    bool ForcedDecodeAvailable { get; }

    SegmentProbabilities? Capture(string segmentIdentity, Completion completion);

    IRuntimeProbabilityPort Port();
}

public sealed class HostProbabilityCapture : IProbabilityCapture, ITokenLogprobSource
{
    private readonly Dictionary<string, SegmentProbabilities> _captured = new(StringComparer.Ordinal);

    public ProbabilityAvailability Availability { get; } = ProbabilityAvailability.Unavailable(UnavailableProbabilityPort.HostProtocolReason);

    public bool ForcedDecodeAvailable => false;

    public SegmentProbabilities? Capture(string segmentIdentity, Completion completion)
    {
        ArgumentNullException.ThrowIfNull(segmentIdentity);

        if (completion.Faulted || completion.Probabilities is not { Count: > 0 } probabilities)
        {
            return null;
        }

        var segment = new SegmentProbabilities(segmentIdentity, completion.Text, probabilities);
        _captured[segmentIdentity] = segment;

        return segment;
    }

    public IRuntimeProbabilityPort Port() =>
        _captured.Count == 0
            ? new UnavailableProbabilityPort(Availability.Reason)
            : new CapturedProbabilityPort(_captured.Values.OrderBy(s => s.SegmentIdentity, StringComparer.Ordinal));

    public bool TryGetLogprobs(string requestId, out IReadOnlyList<(string Token, double LogProb)> logprobs, out string reason)
    {
        if (_captured.TryGetValue(requestId, out var segment))
        {
            logprobs = [.. segment.Tokens.Select(t => (t.Text, t.LogProb))];
            reason = string.Empty;
            return true;
        }

        logprobs = [];
        reason = Availability.Reason;
        return false;
    }
}
