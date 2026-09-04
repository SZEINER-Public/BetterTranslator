using System.Globalization;
using System.Text;

namespace BetterTranslator.Core.Verification.Gate;

public static class GateRunReport
{
    public const string DefaultDirectory = "artifacts";

    public const string DefaultFileName = "gate-run.log";

    public static string Render(GateRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        builder.AppendLine(culture, $"completion {result.CompletionText}%");
        builder.AppendLine(culture, $"exempt {result.ExemptCount}");
        builder.AppendLine(culture, $"routed {result.Routed.Count}");
        builder.AppendLine(culture, $"defects {result.Defects.Count}");
        builder.AppendLine(culture, $"repair candidates {result.RepairCandidates.Count}");
        builder.AppendLine(culture, $"score contributions {result.ScoreContributions.Count}");
        builder.AppendLine(culture, $"checks ran {result.Ran}, skipped {result.Skipped}");
        builder.AppendLine(culture, $"categories present {string.Join(",", result.Categories)}");
        builder.AppendLine(culture, $"categories skipped {string.Join(",", result.SkippedCategories)}");
        builder.AppendLine(culture, $"routing {result.RoutingText}");
        builder.AppendLine(culture, $"stages {result.StageText}");
        builder.AppendLine(culture, $"caps {result.CapsText}");
        builder.AppendLine("per-check counts");

        foreach (var (checkId, count) in result.FindingCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(culture, $"  {checkId} {count}");
        }

        builder.AppendLine("skipped");

        foreach (var status in result.Checks.Where(c => c.State == GateCheckState.Skipped).OrderBy(c => c.Stage).ThenBy(c => c.CheckId, StringComparer.Ordinal))
        {
            builder.AppendLine(culture, $"  {status.CheckId} [{status.Stage}] {status.Reason}");
        }

        builder.AppendLine("red tier");

        foreach (var defect in result.Defects)
        {
            builder.AppendLine(culture, $"  {defect.CheckId} {defect.TargetRange.UnitPath}@{defect.TargetRange.Offset}+{defect.TargetRange.Length} confidence {defect.Confidence} action {defect.Action} causes {string.Join("+", defect.CauseCodes)}");
        }

        builder.AppendLine("score contributions");

        foreach (var contribution in result.ScoreContributions)
        {
            builder.AppendLine(culture, $"  {contribution.CheckId} {contribution.TargetRange.UnitPath}@{contribution.TargetRange.Offset}+{contribution.TargetRange.Length} confidence {contribution.Confidence} priority {contribution.Priority}");
        }

        return builder.ToString();
    }

    public static string Write(GateRunResult result, string? directory = null, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var folder = directory ?? DefaultDirectory;
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, fileName ?? DefaultFileName);
        File.WriteAllText(path, Render(result), new UTF8Encoding(false));

        return path;
    }
}
