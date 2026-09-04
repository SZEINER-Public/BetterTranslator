using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Runtime;

public sealed record LowProbabilityRun(int FirstToken, int LastToken, int Offset, int Length, double MeanLogProb)
{
    public int TokenCount => LastToken - FirstToken + 1;

    public int Confidence => (int)Math.Clamp(Math.Round(100 - 100 * Math.Exp(MeanLogProb), MidpointRounding.AwayFromZero), 0, 100);
}

public sealed record CapturedSegment(SegmentAlignment Alignment, SegmentProbabilities Probabilities, bool ExactMap, string SourceText, string TargetText);

public abstract class RuntimeCheck : ICheck
{
    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Runtime.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var port = RuntimePorts.For(context);

        if (SkipReason(context, port) is not null)
        {
            return [];
        }

        var findings = new List<CheckFinding>();

        foreach (var segment in Segments(context, port))
        {
            Find(context, port, segment, findings);
        }

        return
        [
            .. findings
                .Where(finding => !context.Exemptions.IsExempt(finding.TargetRange))
                .OrderBy(finding => finding.TargetRange.Offset)
                .ThenBy(finding => finding.TargetRange.Length)
                .ThenBy(finding => finding.Evidence, StringComparer.Ordinal),
        ];
    }

    public virtual string? SkipReason(CheckContext context, IRuntimeProbabilityPort port)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(port);

        if (!port.Availability.Available)
        {
            return port.Availability.Reason;
        }

        return port.CapturedSegments.Count == 0 ? "no segment probabilities were captured for this run" : null;
    }

    protected abstract void Find(CheckContext context, IRuntimeProbabilityPort port, CapturedSegment segment, List<CheckFinding> findings);

    protected CheckFinding Finding(CheckRange target, CheckRange? source, CheckGranularity granularity, int confidence, string evidence) =>
        new(CheckId, target, source, granularity, CheckSeverity.Score, confidence, CheckCause.ModelOutput, evidence, CheckAction.ScoreOnly);

    public static IReadOnlyList<CapturedSegment> Segments(CheckContext context, IRuntimeProbabilityPort port)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(port);

        var segments = new List<CapturedSegment>();

        foreach (var alignment in context.Alignment.OrderBy(s => s.SourceRange.Offset).ThenBy(s => s.Identity, StringComparer.Ordinal))
        {
            if (alignment.TargetRange is null || port.Captured(alignment.Identity) is not { } probabilities || probabilities.Count == 0)
            {
                continue;
            }

            var targetText = Slice(context.Target.Text, alignment.TargetRange);
            var sourceText = Slice(context.Source.Text, alignment.SourceRange);
            var exact = string.Equals(targetText, probabilities.Answer, StringComparison.Ordinal)
                && probabilities.Tokens.All(t => t.Offset >= 0 && t.Offset + t.Length <= probabilities.Answer.Length);

            segments.Add(new CapturedSegment(alignment, probabilities, exact, sourceText, targetText));
        }

        return segments;
    }

    public static IReadOnlyList<LowProbabilityRun> LowRuns(SegmentProbabilities probabilities)
    {
        ArgumentNullException.ThrowIfNull(probabilities);

        var tokens = probabilities.Tokens;

        if (tokens.Count < 2)
        {
            return [];
        }

        var mean = probabilities.MeanLogProb;
        var deviation = Math.Sqrt(tokens.Average(t => (t.LogProb - mean) * (t.LogProb - mean)));

        if (deviation == 0)
        {
            return [];
        }

        var floor = mean - deviation;
        var runs = new List<LowProbabilityRun>();
        var start = -1;

        for (var i = 0; i <= tokens.Count; i++)
        {
            var low = i < tokens.Count && tokens[i].LogProb < floor;

            if (low && start < 0)
            {
                start = i;
            }

            if (low || start < 0)
            {
                continue;
            }

            var first = tokens[start];
            var last = tokens[i - 1];
            var runMean = tokens.Skip(start).Take(i - start).Average(t => t.LogProb);

            runs.Add(new LowProbabilityRun(start, i - 1, first.Offset, last.Offset + last.Length - first.Offset, runMean));
            start = -1;
        }

        return runs;
    }

    protected static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Slice(string text, CheckRange range)
    {
        var offset = Math.Clamp(range.Offset, 0, text.Length);
        var length = Math.Clamp(range.Length, 0, text.Length - offset);

        return text.Substring(offset, length);
    }
}

public sealed class SequenceConfidenceCheck : RuntimeCheck
{
    public override string CheckId => Checks.CheckId.Runtime.SequenceConfidence;

    protected override void Find(CheckContext context, IRuntimeProbabilityPort port, CapturedSegment segment, List<CheckFinding> findings)
    {
        var confidence = (int)Math.Clamp(Math.Round(100 - segment.Probabilities.SequenceConfidence, MidpointRounding.AwayFromZero), 0, 100);

        if (confidence == 0)
        {
            return;
        }

        findings.Add(Finding(
            segment.Alignment.TargetRange!,
            segment.Alignment.SourceRange,
            CheckGranularity.Sentence,
            confidence,
            $"sequence confidence {Number(segment.Probabilities.SequenceConfidence)} over {segment.Probabilities.Count} tokens, mean logprob {Number(segment.Probabilities.MeanLogProb)}, min logprob {Number(segment.Probabilities.MinLogProb)}"));
    }
}

public sealed class SpanLocalizationCheck : RuntimeCheck
{
    public override string CheckId => Checks.CheckId.Runtime.SpanLocalization;

    protected override void Find(CheckContext context, IRuntimeProbabilityPort port, CapturedSegment segment, List<CheckFinding> findings)
    {
        var targetRange = segment.Alignment.TargetRange!;

        foreach (var run in LowRuns(segment.Probabilities))
        {
            if (run.Confidence == 0)
            {
                continue;
            }

            var tokens = $"tokens {run.FirstToken} to {run.LastToken}";

            if (segment.ExactMap)
            {
                findings.Add(Finding(
                    new CheckRange(targetRange.UnitPath, targetRange.Offset + run.Offset, run.Length),
                    segment.Alignment.SourceRange,
                    CheckGranularity.Word,
                    run.Confidence,
                    $"low-probability run {tokens} of {segment.Probabilities.Count}, mean logprob {Number(run.MeanLogProb)}"));
                continue;
            }

            findings.Add(Finding(
                targetRange,
                segment.Alignment.SourceRange,
                CheckGranularity.Sentence,
                run.Confidence,
                $"low-probability run {tokens} of {segment.Probabilities.Count}, mean logprob {Number(run.MeanLogProb)}; offset map is lossy for this segment"));
        }
    }
}

public sealed class ForcedDecodeAdequacyCheck : RuntimeCheck
{
    public override string CheckId => Checks.CheckId.Runtime.ForcedDecodeAdequacy;

    public override string? SkipReason(CheckContext context, IRuntimeProbabilityPort port)
    {
        var inherited = base.SkipReason(context, port);

        if (inherited is not null)
        {
            return inherited;
        }

        return port.ForcedDecodeAvailable ? null : "forced-decode scoring is not exposed by the runtime";
    }

    protected override void Find(CheckContext context, IRuntimeProbabilityPort port, CapturedSegment segment, List<CheckFinding> findings)
    {
        if (LowRuns(segment.Probabilities).Count == 0)
        {
            return;
        }

        var score = port.ScoreForced(segment.Alignment.Identity, segment.SourceText, segment.TargetText);

        if (score is null)
        {
            return;
        }

        var confidence = (int)Math.Clamp(Math.Round(100 - score.Adequacy, MidpointRounding.AwayFromZero), 0, 100);

        if (confidence == 0)
        {
            return;
        }

        findings.Add(Finding(
            segment.Alignment.TargetRange!,
            segment.Alignment.SourceRange,
            CheckGranularity.Sentence,
            confidence,
            $"forced-decode adequacy {Number(score.Adequacy)}, length-normalized logprob {Number(score.LengthNormalizedLogProb)} over {score.ScoredTokens} tokens"));
    }
}
