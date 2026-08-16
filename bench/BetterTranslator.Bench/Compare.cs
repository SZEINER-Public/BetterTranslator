using System.Text;
using System.Text.Json.Nodes;

namespace BetterTranslator.Bench;

internal static class Compare
{
    private const string Baseline = "direct";
    private const string Delegated = "mcp-path";

    internal static string Write(string runId, IReadOnlyList<RunSummary> summaries)
    {
        var environment = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.RunDirectory(runId), "environment.json")))!;
        var billed = summaries.Where(summary => summary.Rep > 1 && summary.TotalTokens > 0).ToList();
        var measured = billed.Where(summary => summary.TranslatedValues > 0).ToList();
        var keys = billed.Select(summary => summary.Keys).Distinct().OrderBy(value => value).ToList();
        var builder = new StringBuilder();

        builder.AppendLine("# Claude Code CLI against Claude Code CLI with the local BetterTranslator MCP server");
        builder.AppendLine();
        builder.AppendLine($"Run `{runId}`. Both arms are the same CLI, the same model ({Text(environment["Model"])}), the same prompt apart from one sentence, the same slice and the same fresh session per run. The only difference is who translates.");
        builder.AppendLine();
        builder.AppendLine($"- **{Baseline}**: Claude Code translates the values itself. No MCP server is configured (`--strict-mcp-config` with no config).");
        builder.AppendLine($"- **{Delegated}**: Claude Code calls `translate_file` on the BetterTranslator MCP server over stdio and polls `job_status`. The translation is done on this machine by {Text(environment["Mcp"]?["SelectedModel"])} ({Text(environment["Mcp"]?["SelectedModelFile"])}), and the file content never enters the conversation.");
        builder.AppendLine();
        builder.AppendLine("Repetition 1 is the cache warm-up and is excluded. Token figures come from Claude's own accounting; USD figures are the CLI's approximation, not a billing figure.");
        builder.AppendLine();

        builder.AppendLine("## Head to head");
        builder.AppendLine();
        builder.AppendLine("| slice | arm | runs | requests | cacheCreation | cacheRead | total tokens | approx USD | wall clock s | values delivered |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var key in keys)
        {
            foreach (var arm in new[] { Baseline, Delegated })
            {
                var rows = billed.Where(summary => summary.Arm == arm && summary.Keys == key).ToList();

                if (rows.Count == 0)
                {
                    builder.AppendLine($"| slice-{key} | {arm} | 0 | no run | | | | | | |");
                    continue;
                }

                var delivered = rows.Average(row => (double)row.TranslatedValues);
                var total = rows.Max(row => row.TotalValues);

                builder.AppendLine($"| slice-{key} | {arm} | {rows.Count} | {rows.Average(row => (double)row.Requests):0.0} | {rows.Average(row => (double)row.CacheCreationTokens):0} | {rows.Average(row => (double)row.CacheReadTokens):0} | {rows.Average(row => (double)row.TotalTokens):0} | {rows.Average(row => row.ApproximateCostUsd):0.0000} | {rows.Average(row => row.WallClockSeconds):0.0} | {(total == 0 ? "**none**" : $"{delivered:0} of {total}")} |");
            }
        }

        builder.AppendLine();

        var undelivered = billed
            .Where(summary => summary.Arm == Delegated && summary.TranslatedValues == 0)
            .Select(summary => summary.Keys)
            .Distinct()
            .ToList();

        if (undelivered.Count > 0)
        {
            builder.AppendLine($"At {string.Join(" and ", undelivered.Select(key => $"slice-{key}"))} the delegated arm billed requests and produced no file. The local job was still running when the agent stopped waiting, and the MCP server dies with the session that started it, so the work was thrown away. Those slices are a delivery failure, not a cheap run, and they are excluded from the difference table below.");
            builder.AppendLine();
        }
        builder.AppendLine("## What delegating changes");
        builder.AppendLine();
        builder.AppendLine("| slice | delta tokens | delta percent | delta USD | delta percent | delta requests | delta wall clock s |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var key in keys)
        {
            var baseline = Mean(measured, Baseline, key);
            var delegated = Mean(measured, Delegated, key);

            if (baseline is null || delegated is null)
            {
                builder.AppendLine($"| slice-{key} | not comparable | | | | | |");
                continue;
            }

            builder.AppendLine(
                $"| slice-{key} | {delegated.Tokens - baseline.Tokens:+0;-0;0} | {(delegated.Tokens - baseline.Tokens) * 100 / baseline.Tokens:+0.0;-0.0;0.0}% "
                + $"| {delegated.Cost - baseline.Cost:+0.0000;-0.0000;0} | {(delegated.Cost - baseline.Cost) * 100 / baseline.Cost:+0.0;-0.0;0.0}% "
                + $"| {delegated.Requests - baseline.Requests:+0.0;-0.0;0} | {delegated.Wall - baseline.Wall:+0.0;-0.0;0} |");
        }

        builder.AppendLine();

        var sourceKeys = (int)(environment["Corpus"]?["SourceKeys"]?.GetValue<double>() ?? 0);
        var largest = keys
            .Where(key => measured.Any(row => row.Arm == Baseline && row.Keys == key)
                && measured.Any(row => row.Arm == Delegated && row.Keys == key))
            .DefaultIfEmpty(0)
            .Max();
        var chunks = largest == 0 ? 0 : (int)Math.Ceiling(sourceKeys / (double)largest);
        var baselineBig = Mean(measured, Baseline, largest);
        var delegatedBig = Mean(measured, Delegated, largest);

        builder.AppendLine($"## The whole file, {sourceKeys} keys");
        builder.AppendLine();
        builder.AppendLine($"Projected as {chunks} chunks of {largest} keys, because the file does not fit one context window. Arithmetic on the measured runs, not a measured run.");
        builder.AppendLine();
        builder.AppendLine("| path | tokens | approx USD | wall clock | Claude requests |");
        builder.AppendLine("|---|---:|---:|---:|---:|");

        if (baselineBig is not null)
        {
            builder.AppendLine($"| Claude Code alone | {baselineBig.Tokens * chunks:0} | {baselineBig.Cost * chunks:0.00} | {TimeSpan.FromSeconds(baselineBig.Wall * chunks):h\\hmm\\m} | {baselineBig.Requests * chunks:0} |");
        }

        if (delegatedBig is not null)
        {
            builder.AppendLine($"| Claude Code plus local BetterTranslator | {delegatedBig.Tokens * chunks:0} | {delegatedBig.Cost * chunks:0.00} | {TimeSpan.FromSeconds(delegatedBig.Wall * chunks):h\\hmm\\m} | {delegatedBig.Requests * chunks:0} |");
        }

        var localSeconds = delegatedBig is null || largest == 0 ? 0 : delegatedBig.Wall / largest;

        builder.AppendLine($"| BetterTranslator alone, no agent | 0 | 0.00 | {TimeSpan.FromSeconds(localSeconds * sourceKeys):h\\hmm\\m} | 0 |");
        builder.AppendLine();

        builder.AppendLine("## Reproducing this");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine("btbench prepare --source <path to en.json> --model claude-opus-5 --sizes 10,50,200,500");
        builder.AppendLine("btbench run --run <run-id> --arms direct,mcp-path --reps 3");
        builder.AppendLine("btbench compare --run <run-id>");
        builder.AppendLine("```");
        builder.AppendLine();

        Files.WriteText(Path.Combine(Paths.RunDirectory(runId), "compare.md"), builder.ToString());

        if (baselineBig is null || delegatedBig is null)
        {
            return "COMPARE: the largest slice has no comparable pair of runs.";
        }

        var tokenPercent = (delegatedBig.Tokens - baselineBig.Tokens) * 100 / baselineBig.Tokens;
        var cashPercent = (delegatedBig.Cost - baselineBig.Cost) * 100 / baselineBig.Cost;

        return $"COMPARE at {largest} keys: delegating to the local MCP server spends {tokenPercent:+0.0;-0.0}% tokens and {cashPercent:+0.0;-0.0}% cash against Claude Code translating itself; not using an agent at all saves 100% of both.";
    }

    private static Aggregate? Mean(IReadOnlyList<RunSummary> rows, string arm, int keys)
    {
        var selected = rows.Where(row => row.Arm == arm && row.Keys == keys).ToList();

        return selected.Count == 0
            ? null
            : new Aggregate(
                selected.Average(row => (double)row.TotalTokens),
                selected.Average(row => row.ApproximateCostUsd),
                selected.Average(row => (double)row.Requests),
                selected.Average(row => row.WallClockSeconds));
    }

    private static string Text(JsonNode? node) => node?.ToString() ?? string.Empty;

    private sealed record Aggregate(double Tokens, double Cost, double Requests, double Wall);
}
