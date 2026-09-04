using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Runtime;

public enum RuntimeCheckState
{
    Ran,
    Skipped,
}

public sealed record RuntimeCheckStatus(string CheckId, RuntimeCheckState State, string Reason, int Findings)
{
    public bool CountsTowardPassTotal => State == RuntimeCheckState.Ran;
}

public sealed record RuntimeRunReport(
    ProbabilityAvailability Availability,
    IReadOnlyList<RuntimeCheckStatus> Checks,
    IReadOnlyList<CheckFinding> Findings,
    double? MeanSequenceConfidence,
    int CapturedSegments,
    int ForcedDecodeCalls)
{
    public int Ran => Checks.Count(c => c.State == RuntimeCheckState.Ran);

    public int Skipped => Checks.Count(c => c.State == RuntimeCheckState.Skipped);

    public string Summary()
    {
        var confidence = MeanSequenceConfidence is { } mean ? mean.ToString("0.0", CultureInfo.InvariantCulture) : "unavailable";
        var availability = Availability.Available ? "per-token logprobs available" : "per-token logprobs unavailable: " + Availability.Reason;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{availability}; {Ran} ran, {Skipped} skipped, {Findings.Count} findings, mean sequence confidence {confidence}, {CapturedSegments} captured segments, {ForcedDecodeCalls} forced decodes");
    }

    public static RuntimeRunReport Build(CheckContext context, IEnumerable<RuntimeCheck>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var port = RuntimePorts.For(context);
        var statuses = new List<RuntimeCheckStatus>();
        var findings = new List<CheckFinding>();

        foreach (var check in checks ?? Defaults())
        {
            var reason = check.SkipReason(context, port);

            if (reason is not null)
            {
                statuses.Add(new RuntimeCheckStatus(check.CheckId, RuntimeCheckState.Skipped, reason, 0));
                continue;
            }

            var produced = check.Run(context);
            findings.AddRange(produced);
            statuses.Add(new RuntimeCheckStatus(check.CheckId, RuntimeCheckState.Ran, string.Empty, produced.Count));
        }

        var segments = port.Availability.Available ? RuntimeCheck.Segments(context, port) : [];
        double? mean = segments.Count == 0 ? null : Math.Round(segments.Average(s => s.Probabilities.SequenceConfidence), 1, MidpointRounding.AwayFromZero);

        return new RuntimeRunReport(port.Availability, statuses, findings, mean, segments.Count, port.ForcedDecodeCalls);
    }

    private static IEnumerable<RuntimeCheck> Defaults() =>
    [
        new SequenceConfidenceCheck(),
        new SpanLocalizationCheck(),
        new ForcedDecodeAdequacyCheck(),
    ];
}
