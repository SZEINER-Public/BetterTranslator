using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Engine.Verification;

public static class RuntimeScoring
{
    public static void Apply(IReadOnlyList<VerificationSpan> spans, IReadOnlyList<CheckFinding> findings, double weight)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(findings);
        CheckInstrumentation.Hit("scoring/runtime");

        var ordered = findings
            .Where(f => f.CheckId.StartsWith(CheckId.Runtime.Category + "-", StringComparison.Ordinal))
            .Where(f => f.Severity == CheckSeverity.Score && f.Action != CheckAction.Repair)
            .OrderBy(f => f.TargetRange.Offset)
            .ThenBy(f => f.TargetRange.Length)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal);

        foreach (var finding in ordered)
        {
            var penalty = (int)Math.Round(finding.Confidence * weight, MidpointRounding.AwayFromZero);

            if (penalty <= 0)
            {
                continue;
            }

            foreach (var span in spans)
            {
                if (span.Exempt || span.Start + span.Length <= finding.TargetRange.Offset || span.Start >= finding.TargetRange.End)
                {
                    continue;
                }

                span.Signals.Add(new SignalHit(finding.CheckId, finding.CheckId, penalty, finding.Evidence));
                span.Score = Math.Max(0, span.Score - penalty);
            }
        }
    }
}
