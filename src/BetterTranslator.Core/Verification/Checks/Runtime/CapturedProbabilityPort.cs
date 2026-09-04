namespace BetterTranslator.Core.Verification.Checks.Runtime;

public delegate AdequacyScore? ForcedDecodeScorer(string segmentIdentity, string source, string target);

public sealed class CapturedProbabilityPort : IRuntimeProbabilityPort
{
    private readonly Dictionary<string, SegmentProbabilities> _segments = new(StringComparer.Ordinal);

    private readonly Dictionary<string, AdequacyScore?> _forced = new(StringComparer.Ordinal);

    private readonly ForcedDecodeScorer? _scorer;

    private int _forcedCalls;

    public CapturedProbabilityPort(IEnumerable<SegmentProbabilities> segments, ForcedDecodeScorer? scorer = null)
    {
        ArgumentNullException.ThrowIfNull(segments);

        foreach (var segment in segments)
        {
            _segments[segment.SegmentIdentity] = segment;
        }

        _scorer = scorer;
    }

    public ProbabilityAvailability Availability => ProbabilityAvailability.Ready;

    public IReadOnlyList<string> CapturedSegments => [.. _segments.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    public int ForcedDecodeCalls => _forcedCalls;

    public bool ForcedDecodeAvailable => _scorer is not null;

    public SegmentProbabilities? Captured(string segmentIdentity) =>
        _segments.TryGetValue(segmentIdentity, out var segment) ? segment : null;

    public AdequacyScore? ScoreForced(string segmentIdentity, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(segmentIdentity);

        if (_forced.TryGetValue(segmentIdentity, out var cached))
        {
            return cached;
        }

        if (_scorer is null)
        {
            return null;
        }

        _forcedCalls++;
        var score = _scorer(segmentIdentity, source, target);
        _forced[segmentIdentity] = score;

        return score;
    }
}
