using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Core.Verification.Gate;

public static class ExemptionFilterRule
{
    public const string RuleId = Checks.CheckId.Gate.ExemptionFilter;

    public static (IReadOnlyList<CheckFinding> Kept, int Exempt) Apply(CheckContext context, IEnumerable<CheckFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(findings);

        var kept = new List<CheckFinding>();
        var exempt = 0;

        foreach (var finding in findings)
        {
            if (Intersects(context, finding))
            {
                exempt++;
                continue;
            }

            kept.Add(finding);
        }

        return (kept, exempt);
    }

    public static bool Intersects(CheckContext context, CheckFinding finding)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(finding);

        return context.Exemptions.Spans.Any(span => span.Range.Overlaps(finding.TargetRange));
    }
}

public static class RoutingPolicyRule
{
    public const string RuleId = Checks.CheckId.Gate.RoutingPolicy;

    public static IReadOnlyList<RoutedFinding> Apply(RoutingTable routing, StageTable stages, IEnumerable<CheckFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(findings);

        var routed = new List<RoutedFinding>();

        foreach (var finding in findings)
        {
            var category = RoutingTable.CategoryOf(finding.CheckId);
            routed.Add(RoutedFinding.From(finding, stages.StageOf(category), routing.ActionFor(finding)));
        }

        return RaisePriority(routing, routed);
    }

    private static IReadOnlyList<RoutedFinding> RaisePriority(RoutingTable routing, List<RoutedFinding> routed)
    {
        var raisers = routed.Where(r => routing.RaisesPriority(r.Finding)).ToList();

        if (raisers.Count == 0)
        {
            return routed;
        }

        var result = new List<RoutedFinding>(routed.Count);

        foreach (var candidate in routed)
        {
            if (routing.RaisesPriority(candidate.Finding))
            {
                result.Add(candidate);
                continue;
            }

            var raised = raisers.Count(r =>
                r.Stage > candidate.Stage
                && string.Equals(r.TargetRange.UnitPath, candidate.TargetRange.UnitPath, StringComparison.Ordinal)
                && r.TargetRange.Overlaps(candidate.TargetRange));

            result.Add(raised == 0 ? candidate : candidate with { Priority = candidate.Priority + raised });
        }

        return result;
    }
}

public static class DeduplicationRule
{
    public const string RuleId = Checks.CheckId.Gate.Deduplication;

    public static IReadOnlyList<RoutedFinding> Apply(IEnumerable<RoutedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var merged = new List<RoutedFinding>();

        foreach (var group in findings.GroupBy(f => (f.TargetRange.UnitPath, f.Granularity)).OrderBy(g => g.Key.UnitPath, StringComparer.Ordinal).ThenBy(g => g.Key.Granularity))
        {
            var ordered = group.OrderBy(f => f.TargetRange.Offset).ThenBy(f => f.TargetRange.Length).ThenBy(f => f.CheckId, StringComparer.Ordinal).ToList();
            var cluster = new List<RoutedFinding>();
            var clusterEnd = int.MinValue;

            foreach (var finding in ordered)
            {
                if (cluster.Count > 0 && finding.TargetRange.Offset < clusterEnd && finding.TargetRange.Length > 0)
                {
                    cluster.Add(finding);
                    clusterEnd = Math.Max(clusterEnd, finding.TargetRange.End);
                    continue;
                }

                if (cluster.Count > 0)
                {
                    merged.Add(Merge(cluster));
                }

                cluster = [finding];
                clusterEnd = finding.TargetRange.End;
            }

            if (cluster.Count > 0)
            {
                merged.Add(Merge(cluster));
            }
        }

        return
        [
            .. merged
                .OrderBy(f => f.TargetRange.UnitPath, StringComparer.Ordinal)
                .ThenBy(f => f.TargetRange.Offset)
                .ThenBy(f => f.TargetRange.Length)
                .ThenBy(f => f.CheckId, StringComparer.Ordinal),
        ];
    }

    public static RoutedFinding Merge(IReadOnlyList<RoutedFinding> cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (cluster.Count == 1)
        {
            return cluster[0];
        }

        var carrier = cluster
            .OrderByDescending(f => f.Confidence)
            .ThenBy(f => f.Severity)
            .ThenBy(f => f.Stage)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .First();

        var severity = cluster.Min(f => f.Severity);
        var action = cluster.Min(f => f.Action);
        var priority = cluster.Sum(f => f.Priority);
        var checkIds = cluster.SelectMany(f => f.CheckIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var causes = cluster.SelectMany(f => f.CauseCodes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stage = cluster.Min(f => f.Stage);
        var granularity = cluster.Max(f => f.Granularity);

        var finding = carrier.Finding with
        {
            Severity = severity,
            Action = action,
            Granularity = granularity,
            CauseCode = string.Join("+", causes),
        };

        return carrier with
        {
            Finding = finding,
            Stage = stage,
            Action = action,
            Priority = priority,
            CheckIds = checkIds,
            CauseCodes = causes,
        };
    }
}

public static class CandidateOrderingRule
{
    public const string RuleId = Checks.CheckId.Gate.CandidateOrdering;

    public static IReadOnlyList<RoutedFinding> Apply(IEnumerable<RoutedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var candidates = findings.Where(f => f.IsRepairCandidate).ToList();
        var survivors = candidates
            .Where(candidate => !candidates.Any(coarser => Shadows(coarser, candidate)))
            .OrderBy(f => Rank(f.Granularity))
            .ThenByDescending(f => f.Confidence)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.TargetRange.UnitPath, StringComparer.Ordinal)
            .ThenBy(f => f.TargetRange.Offset)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();

        return survivors;
    }

    public static int Rank(CheckGranularity granularity) => granularity switch
    {
        CheckGranularity.Document => 0,
        CheckGranularity.Block => 1,
        CheckGranularity.Sentence => 2,
        _ => 3,
    };

    private static bool Shadows(RoutedFinding coarser, RoutedFinding finer) =>
        !ReferenceEquals(coarser, finer)
        && Rank(coarser.Granularity) < Rank(finer.Granularity)
        && string.Equals(coarser.TargetRange.UnitPath, finer.TargetRange.UnitPath, StringComparison.Ordinal)
        && coarser.TargetRange.Contains(finer.TargetRange);
}

public static class CapsRule
{
    public const string RuleId = Checks.CheckId.Gate.Caps;

    public static IReadOnlyList<RoutedFinding> CapCandidates(IReadOnlyList<RoutedFinding> ordered, int cap)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        return cap <= 0 ? ordered : [.. ordered.Take(cap)];
    }

    public static IReadOnlyList<RoutedFinding> CapPerUnit(IEnumerable<RoutedFinding> findings, int cap)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var kept = new List<RoutedFinding>();

        foreach (var group in findings.GroupBy(f => f.TargetRange.UnitPath, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(f => f.Severity)
                .ThenByDescending(f => f.Confidence)
                .ThenByDescending(f => f.Priority)
                .ThenBy(f => f.TargetRange.Offset)
                .ThenBy(f => f.CheckId, StringComparer.Ordinal);

            kept.AddRange(cap <= 0 ? ordered : ordered.Take(cap));
        }

        return
        [
            .. kept
                .OrderBy(f => f.TargetRange.UnitPath, StringComparer.Ordinal)
                .ThenBy(f => f.TargetRange.Offset)
                .ThenBy(f => f.TargetRange.Length)
                .ThenBy(f => f.CheckId, StringComparer.Ordinal),
        ];
    }
}
