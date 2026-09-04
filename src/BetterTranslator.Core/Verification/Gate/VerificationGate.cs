using System.Globalization;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Coverage;

namespace BetterTranslator.Core.Verification.Gate;

public sealed record GateRunResult(
    double CompletionPercent,
    int ExemptCount,
    IReadOnlyList<RoutedFinding> Routed,
    IReadOnlyList<RoutedFinding> Defects,
    IReadOnlyList<RoutedFinding> RepairCandidates,
    IReadOnlyList<ScoreContribution> ScoreContributions,
    IReadOnlyDictionary<string, int> FindingCounts,
    IReadOnlyList<GateCheckStatus> Checks,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> SkippedCategories,
    string RoutingText,
    string StageText,
    string CapsText)
{
    public const string RuleId = Core.Verification.Checks.CheckId.Gate.RunResult;

    public EscalationSummary? Escalation { get; init; }

    public string CompletionText => CompletionPercent.ToString("0.0", CultureInfo.InvariantCulture);

    public int Ran => Checks.Count(c => c.State == GateCheckState.Ran);

    public int Skipped => Checks.Count(c => c.State == GateCheckState.Skipped);

    public IReadOnlyList<CheckFinding> Findings => [.. Routed.Select(r => r.Projected)];

    public IReadOnlyList<CheckFinding> RedTier => [.. Defects.Select(r => r.Projected)];

    public static GateRunResult Empty(string reason) =>
        new(100.0, 0, [], [], [], [], new Dictionary<string, int>(StringComparer.Ordinal), [new GateCheckStatus("GATE", "GATE", GateStage.Deterministic, GateCheckState.Skipped, reason, 0)], [], [], RoutingTable.DefaultText, StageTable.DefaultText, string.Empty);
}

public sealed class VerificationGate
{
    private readonly CheckRegistry _registry;
    private readonly GateSettings _settings;

    public VerificationGate(CheckRegistry? registry = null, GateSettings? settings = null)
    {
        _registry = registry ?? CheckRegistry.Default;
        _settings = settings ?? new GateSettings();
    }

    public GateSettings Settings => _settings;

    public Action<CheckContext, IReadOnlyList<CheckFinding>>? BeforeEscalation { get; init; }

    public GateRunResult Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_settings.Enabled)
        {
            CheckInstrumentation.Hit("config/gate-disabled");
            return GateRunResult.Empty("gate disabled by setting");
        }

        var routing = _settings.Routing;
        var stages = _settings.Stages;
        var statuses = new List<GateCheckStatus>();
        var raw = new List<CheckFinding>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        var discovered = _registry.Checks
            .GroupBy(check => check.Category, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CheckId, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var known = stages.Categories
            .Concat(routing.Categories)
            .Concat(discovered.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => stages.StageOf(c))
            .ThenBy(c => c, StringComparer.Ordinal)
            .ToList();

        var skippedCategories = new List<string>();
        var escalationOpened = false;

        foreach (var category in known)
        {
            var stage = stages.StageOf(category);

            if (stage == GateStage.Escalation && !escalationOpened)
            {
                escalationOpened = true;
                BeforeEscalation?.Invoke(context, ExemptionFilterRule.Apply(context, raw).Kept);
            }

            if (!discovered.TryGetValue(category, out var checks))
            {
                CheckInstrumentation.Hit("config/category-absent/" + category);
                statuses.Add(new GateCheckStatus(category, category, stage, GateCheckState.Skipped, "no checks discovered for this category", 0));
                skippedCategories.Add(category);
                continue;
            }

            if (!_settings.IsEnabled(category))
            {
                CheckInstrumentation.Hit("config/category-disabled/" + category);
                statuses.AddRange(checks.Select(c => new GateCheckStatus(category, c.CheckId, stage, GateCheckState.Skipped, "category disabled by setting", 0)));
                skippedCategories.Add(category);
                continue;
            }

            foreach (var check in checks)
            {
                if (context.Settings.DisabledChecks.Contains(check.CheckId))
                {
                    CheckInstrumentation.Hit("config/check-disabled/" + check.CheckId);
                    statuses.Add(new GateCheckStatus(category, check.CheckId, stage, GateCheckState.Skipped, "check disabled by run settings", 0));
                    continue;
                }

                if (check is ISkippableCheck skippable && skippable.SkipReason(context) is { } why)
                {
                    CheckInstrumentation.Hit("config/check-skipped/" + check.CheckId);
                    statuses.Add(new GateCheckStatus(category, check.CheckId, stage, GateCheckState.Skipped, why, 0));
                    continue;
                }

                IReadOnlyList<CheckFinding> produced;

                try
                {
                    CheckInstrumentation.Hit("check/" + check.CheckId);
                    produced = check.Run(context);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    statuses.Add(new GateCheckStatus(category, check.CheckId, stage, GateCheckState.Skipped, "check failed: " + ex.GetType().Name, 0));
                    continue;
                }

                counts[check.CheckId] = produced.Count;
                raw.AddRange(produced);
                statuses.Add(new GateCheckStatus(category, check.CheckId, stage, GateCheckState.Ran, string.Empty, produced.Count));
            }
        }

        var (kept, exempt) = ExemptionFilterRule.Apply(context, raw);
        var routed = RoutingPolicyRule.Apply(routing, stages, kept);
        var merged = DeduplicationRule.Apply(routed);
        var capped = CapsRule.CapPerUnit(merged, _settings.FindingsPerUnitCap);
        var candidates = CapsRule.CapCandidates(CandidateOrderingRule.Apply(capped), _settings.RepairCandidateCap);
        var defects = capped.Where(f => f.IsDefect).ToList();
        var contributions = capped
            .Where(f => f.Action != CheckAction.Repair)
            .Select(f => new ScoreContribution(f.CheckId, f.Category, f.TargetRange, f.Confidence, f.Priority, f.Finding.Evidence))
            .ToList();

        CheckInstrumentation.Hit("gate/" + GateRunResult.RuleId);

        return new GateRunResult(
            Completion(context),
            exempt,
            capped,
            defects,
            candidates,
            contributions,
            counts,
            statuses,
            [.. known.Where(discovered.ContainsKey)],
            skippedCategories,
            routing.ToString(),
            stages.ToString(),
            _settings.CapsText());
    }

    private static double Completion(CheckContext context)
    {
        try
        {
            return CompletionMetric.Compute(context).Percent;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0.0;
        }
    }
}
