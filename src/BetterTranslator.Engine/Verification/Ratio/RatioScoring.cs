using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Ratio;

namespace BetterTranslator.Engine.Verification;

public static class RatioScoring
{
    public static void Apply(IReadOnlyList<VerificationSpan> spans, IReadOnlyList<CheckFinding> findings, RatioProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(findings);

        profile ??= RatioProfile.Default;

        var ordered = findings
            .Where(f => f.CheckId.StartsWith(CheckId.Ratio.Category + "-", StringComparison.Ordinal))
            .OrderBy(f => f.TargetRange.Offset)
            .ThenBy(f => f.TargetRange.Length)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal);

        foreach (var finding in ordered)
        {
            var weight = profile.CheckFor(finding.CheckId)?.ScoreWeight ?? 0;
            var penalty = (int)Math.Round(finding.Confidence * weight, MidpointRounding.AwayFromZero);

            if (penalty <= 0)
            {
                continue;
            }

            foreach (var span in spans)
            {
                if (span.Exempt || span.Start < finding.TargetRange.Offset || span.Start >= finding.TargetRange.End)
                {
                    continue;
                }

                span.Signals.Add(new SignalHit(finding.CheckId, finding.CheckId, penalty, finding.Evidence));
                span.Score = Math.Max(0, span.Score - penalty);
            }
        }
    }
}
