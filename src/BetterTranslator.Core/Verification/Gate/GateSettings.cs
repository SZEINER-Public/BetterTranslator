using System.Globalization;
using BetterTranslator.Core.Verification.Checks;

namespace BetterTranslator.Core.Verification.Gate;

public enum GateStage
{
    Deterministic,
    Runtime,
    Escalation,
}

public sealed record RoutingRule(string Category, string? CheckId, CheckSeverity? Severity, CheckAction Action, bool RaisesPriority)
{
    public int Specificity => (CheckId is null ? 0 : 2) + (Severity is null ? 0 : 1);

    public bool Matches(CheckFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (!string.Equals(Category, RoutingTable.CategoryOf(finding.CheckId), StringComparison.Ordinal))
        {
            return false;
        }

        if (CheckId is not null && !string.Equals(CheckId, finding.CheckId, StringComparison.Ordinal))
        {
            return false;
        }

        return Severity is null || Severity == finding.Severity;
    }

    public override string ToString()
    {
        var subject = CheckId ?? Category;
        var severity = Severity?.ToString() ?? "*";
        var action = RaisesPriority ? Action + "+Priority" : Action.ToString();

        return subject + ":" + severity + "=" + action;
    }
}

public sealed class RoutingTable
{
    public const CheckAction FallbackAction = CheckAction.ScoreOnly;

    public const string DefaultText =
        "STR:Defect=Repair;STR:*=Mark;COV:Defect=Repair;COV:*=Mark;TRM:Defect=Repair;TRM:*=Mark;TRM-103:*=Mark;RAT:*=ScoreOnly;SEM:*=ScoreOnly;RUN:*=ScoreOnly+Priority";

    private readonly List<RoutingRule> _rules;

    public RoutingTable(IEnumerable<RoutingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = [.. rules];
    }

    public static RoutingTable Default { get; } = Parse(DefaultText);

    public IReadOnlyList<RoutingRule> Rules => _rules;

    public IReadOnlyList<string> Categories => [.. _rules.Select(r => r.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public RoutingRule? RuleFor(CheckFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return _rules
            .Where(rule => rule.Matches(finding))
            .OrderByDescending(rule => rule.Specificity)
            .ThenBy(rule => rule.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public CheckAction ActionFor(CheckFinding finding) => RuleFor(finding)?.Action ?? FallbackAction;

    public bool RaisesPriority(CheckFinding finding) => RuleFor(finding)?.RaisesPriority ?? false;

    public static string CategoryOf(string checkId)
    {
        ArgumentNullException.ThrowIfNull(checkId);

        var dash = checkId.IndexOf('-', StringComparison.Ordinal);

        return dash < 0 ? checkId : checkId[..dash];
    }

    public static RoutingTable Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var rules = new List<RoutingRule>();

        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rule = ParseRule(entry);

            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        return rules.Count == 0 ? Default : new RoutingTable(rules);
    }

    private static RoutingRule? ParseRule(string entry)
    {
        var equals = entry.IndexOf('=', StringComparison.Ordinal);
        var colon = entry.IndexOf(':', StringComparison.Ordinal);

        if (equals < 0 || colon < 0 || colon > equals)
        {
            return null;
        }

        var subject = entry[..colon].Trim();
        var severityText = entry[(colon + 1)..equals].Trim();
        var actionText = entry[(equals + 1)..].Trim();
        var raises = actionText.EndsWith("+Priority", StringComparison.OrdinalIgnoreCase);

        if (raises)
        {
            actionText = actionText[..^"+Priority".Length];
        }

        if (subject.Length == 0 || !Enum.TryParse<CheckAction>(actionText, true, out var action))
        {
            return null;
        }

        CheckSeverity? severity = null;

        if (severityText != "*")
        {
            if (!Enum.TryParse<CheckSeverity>(severityText, true, out var parsed))
            {
                return null;
            }

            severity = parsed;
        }

        var checkId = subject.Contains('-', StringComparison.Ordinal) ? subject : null;

        return new RoutingRule(CategoryOf(subject), checkId, severity, action, raises);
    }

    public override string ToString() => string.Join(";", _rules.Select(r => r.ToString()));
}

public sealed class StageTable
{
    public const string DefaultText = "Deterministic=STR,COV,TRM;Runtime=RUN;Escalation=RAT,SEM";

    private readonly SortedDictionary<string, GateStage> _stages = new(StringComparer.Ordinal);

    public StageTable(IEnumerable<KeyValuePair<string, GateStage>> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        foreach (var (category, stage) in assignments)
        {
            _stages[category] = stage;
        }
    }

    public static StageTable Default { get; } = Parse(DefaultText);

    public IReadOnlyList<string> Categories => [.. _stages.Keys];

    public GateStage StageOf(string category)
    {
        ArgumentNullException.ThrowIfNull(category);

        return _stages.TryGetValue(category, out var stage) ? stage : GateStage.Escalation;
    }

    public static StageTable Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var assignments = new List<KeyValuePair<string, GateStage>>();

        foreach (var group in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = group.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0 || !Enum.TryParse<GateStage>(group[..equals].Trim(), true, out var stage))
            {
                continue;
            }

            foreach (var category in group[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                assignments.Add(new KeyValuePair<string, GateStage>(category, stage));
            }
        }

        return assignments.Count == 0 ? Default : new StageTable(assignments);
    }

    public override string ToString() =>
        string.Join(";", Enum.GetValues<GateStage>().Select(stage => stage + "=" + string.Join(",", _stages.Where(p => p.Value == stage).Select(p => p.Key))));
}

public sealed class GateSettings
{
    public const int DefaultRepairCandidateCap = 64;

    public const int DefaultFindingsPerUnitCap = 16;

    public bool Enabled { get; set; } = true;

    public int RepairCandidateCap { get; set; } = DefaultRepairCandidateCap;

    public int FindingsPerUnitCap { get; set; } = DefaultFindingsPerUnitCap;

    public string RoutingText { get; set; } = RoutingTable.DefaultText;

    public string StageText { get; set; } = StageTable.DefaultText;

    public string DisabledCategoriesText { get; set; } = string.Empty;

    public RoutingTable Routing => RoutingTable.Parse(RoutingText);

    public StageTable Stages => StageTable.Parse(StageText);

    public IReadOnlySet<string> DisabledCategories =>
        new HashSet<string>(
            DisabledCategoriesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string category)
    {
        ArgumentNullException.ThrowIfNull(category);

        return !DisabledCategories.Contains(category);
    }

    public GateSettings Disable(string category)
    {
        ArgumentNullException.ThrowIfNull(category);

        var set = new SortedSet<string>(DisabledCategories, StringComparer.OrdinalIgnoreCase) { category };
        DisabledCategoriesText = string.Join(",", set);
        return this;
    }

    public string CapsText() =>
        string.Create(CultureInfo.InvariantCulture, $"repair {RepairCandidateCap}, per unit {FindingsPerUnitCap}");
}
