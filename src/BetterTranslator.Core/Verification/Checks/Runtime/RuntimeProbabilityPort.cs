using System.Runtime.CompilerServices;

namespace BetterTranslator.Core.Verification.Checks.Runtime;

public sealed record TokenProbability(int Index, string Text, int Offset, int Length, double LogProb);

public sealed record SegmentProbabilities(string SegmentIdentity, string Answer, IReadOnlyList<TokenProbability> Tokens)
{
    public int Count => Tokens.Count;

    public double MeanLogProb => Tokens.Count == 0 ? 0 : Tokens.Average(t => t.LogProb);

    public double MinLogProb => Tokens.Count == 0 ? 0 : Tokens.Min(t => t.LogProb);

    public double SequenceConfidence => Tokens.Count == 0 ? 0 : Math.Clamp(100 * Math.Exp(MeanLogProb), 0, 100);
}

public sealed record AdequacyScore(string SegmentIdentity, double LengthNormalizedLogProb, int ScoredTokens)
{
    public double Adequacy => Math.Clamp(100 * Math.Exp(LengthNormalizedLogProb), 0, 100);
}

public sealed record ProbabilityAvailability(bool Available, string Reason)
{
    public static ProbabilityAvailability Unavailable(string reason) => new(false, reason);

    public static ProbabilityAvailability Ready { get; } = new(true, string.Empty);
}

public interface IRuntimeProbabilityPort
{
    ProbabilityAvailability Availability { get; }

    IReadOnlyList<string> CapturedSegments { get; }

    SegmentProbabilities? Captured(string segmentIdentity);

    AdequacyScore? ScoreForced(string segmentIdentity, string source, string target);

    bool ForcedDecodeAvailable { get; }

    int ForcedDecodeCalls { get; }
}

public sealed class UnavailableProbabilityPort(string reason) : IRuntimeProbabilityPort
{
    public const string HostProtocolReason =
        "llmster host protocol returns text and token count only and the native runtime exports no per-token logits";

    public static UnavailableProbabilityPort HostProtocol { get; } = new(HostProtocolReason);

    public ProbabilityAvailability Availability { get; } = ProbabilityAvailability.Unavailable(reason);

    public IReadOnlyList<string> CapturedSegments => [];

    public bool ForcedDecodeAvailable => false;

    public int ForcedDecodeCalls => 0;

    public SegmentProbabilities? Captured(string segmentIdentity) => null;

    public AdequacyScore? ScoreForced(string segmentIdentity, string source, string target) => null;
}

public static class RuntimePorts
{
    private static readonly ConditionalWeakTable<CheckContext, IRuntimeProbabilityPort> Attached = new();

    public static void Attach(CheckContext context, IRuntimeProbabilityPort port)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(port);

        Attached.AddOrUpdate(context, port);
    }

    public static IRuntimeProbabilityPort For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Attached.TryGetValue(context, out var port) ? port : UnavailableProbabilityPort.HostProtocol;
    }
}
