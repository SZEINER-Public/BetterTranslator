using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BetterTranslator.Bench;

internal sealed record RunSummary(
    string RunId,
    string Arm,
    string Slice,
    int Keys,
    int Rep,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    int Requests,
    double WallClockSeconds,
    string Validation,
    double ApproximateCostUsd,
    int McpToolCalls,
    long McpToolInputBytes,
    long McpToolResultBytes,
    int AllToolCalls,
    int NumTurns,
    int ExitCode,
    bool TimedOut,
    double StreamVersusResultDeltaPercent,
    string CrossCheck,
    int TranslatedValues,
    int TotalValues,
    string FailureReason,
    string ToolMix,
    string McpResultExcerpt,
    IReadOnlyList<ApiRequestRow> Requests_)
{
    internal bool Counted => Validation == "pass" && Rep > 1;
}

internal static class Collect
{
    internal static IReadOnlyList<RunSummary> Summarise(string runId, IReadOnlyList<RunOutcome> outcomes)
    {
        var summaries = new List<RunSummary>();

        foreach (var outcome in outcomes)
        {
            var parsed = StreamLog.Parse(outcome.Spec.LogPath);
            var validation = Validation.Validate(outcome.Spec.InputPath, outcome.Spec.OutputPath);

            var streamInput = parsed.Requests.Sum(row => row.InputTokens);
            var streamOutput = parsed.Requests.Sum(row => row.OutputTokens);
            var streamCacheCreation = parsed.Requests.Sum(row => row.CacheCreationTokens);
            var streamCacheRead = parsed.Requests.Sum(row => row.CacheReadTokens);
            var streamTotal = streamInput + streamOutput + streamCacheCreation + streamCacheRead;

            var result = parsed.Result;
            var resultTotal = result.InputTokens + result.OutputTokens + result.CacheCreationTokens + result.CacheReadTokens;
            var delta = resultTotal == 0 ? 0 : Math.Abs(streamTotal - resultTotal) * 100.0 / resultTotal;
            var crossCheck = !result.Present
                ? "result-object-missing"
                : delta > 2.0 ? "disagree" : "agree";

            var mcpTools = parsed.Tools.Where(tool => tool.ToolName.StartsWith("mcp__bettertranslator__", StringComparison.Ordinal)).ToList();

            var failures = new List<string>(validation.Failures);

            if (outcome.TimedOut)
            {
                failures.Insert(0, "run timed out");
            }

            if (outcome.ExitCode != 0 && !outcome.TimedOut)
            {
                failures.Insert(0, $"claude exited {outcome.ExitCode}");
            }

            if (result.Present && result.IsError)
            {
                failures.Insert(0, $"result subtype {result.Subtype}");
            }

            if (result.ApiErrorStatus != 0 || result.TerminalReason == "api_error")
            {
                var text = result.ResultText.Trim('"');
                failures.Insert(0, $"API error {result.ApiErrorStatus}: {(text.Length <= 90 ? text : text[..90])}");
            }

            var otherModels = result.ModelUsage
                .Where(usage => usage.OutputTokens > 100 && !usage.Model.Contains("haiku", StringComparison.Ordinal))
                .Select(usage => usage.Model)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (otherModels.Count > 1)
            {
                failures.Insert(0, $"model fallback across {string.Join(" and ", otherModels)}");
            }

            if (outcome.Spec.Arm.StartsWith("mcp", StringComparison.Ordinal) && mcpTools.Count == 0)
            {
                failures.Insert(0, "no BetterTranslator tool result in the run");
            }

            var status = failures.Count == 0 ? "pass" : "fail";

            summaries.Add(new RunSummary(
                RunId: runId,
                Arm: outcome.Spec.Arm,
                Slice: outcome.Spec.Slice,
                Keys: outcome.Spec.SliceKeys,
                Rep: outcome.Spec.Rep,
                InputTokens: streamInput,
                OutputTokens: streamOutput,
                CacheCreationTokens: streamCacheCreation,
                CacheReadTokens: streamCacheRead,
                TotalTokens: streamTotal,
                Requests: parsed.Requests.Count,
                WallClockSeconds: Math.Round(outcome.WallClockMs / 1000.0, 1),
                Validation: status,
                ApproximateCostUsd: result.TotalCostUsd,
                McpToolCalls: mcpTools.Count,
                McpToolInputBytes: mcpTools.Sum(tool => tool.ToolInputSizeBytes),
                McpToolResultBytes: mcpTools.Sum(tool => tool.ToolResultSizeBytes),
                AllToolCalls: parsed.Tools.Count,
                NumTurns: result.NumTurns,
                ExitCode: outcome.ExitCode,
                TimedOut: outcome.TimedOut,
                StreamVersusResultDeltaPercent: Math.Round(delta, 2),
                CrossCheck: crossCheck,
                TranslatedValues: validation.TranslatedValues,
                TotalValues: validation.TotalValues,
                FailureReason: string.Join(" | ", failures),
                ToolMix: string.Join(" ", parsed.Tools
                    .GroupBy(tool => tool.ToolName, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
                    .Select(group => $"{group.Key}x{group.Count()}")),
                McpResultExcerpt: (mcpTools.FirstOrDefault(tool => tool.ToolName.EndsWith("translate_file", StringComparison.Ordinal))
                    ?? mcpTools.FirstOrDefault())?.ResultExcerpt ?? string.Empty,
                Requests_: parsed.Requests));
        }

        return summaries;
    }

    internal static void WriteRaw(string runId, IReadOnlyList<RunSummary> summaries)
    {
        var path = Path.Combine(Paths.RunDirectory(runId), "raw.jsonl");
        var builder = new StringBuilder();

        foreach (var summary in summaries)
        {
            foreach (var request in summary.Requests_)
            {
                var row = new
                {
                    run_id = summary.RunId,
                    arm = summary.Arm,
                    slice = summary.Slice,
                    keys = summary.Keys,
                    rep = summary.Rep,
                    request_index = request.Index,
                    message_id = request.MessageId,
                    model = request.Model,
                    input_tokens = request.InputTokens,
                    output_tokens = request.OutputTokens,
                    cache_creation_tokens = request.CacheCreationTokens,
                    cache_read_tokens = request.CacheReadTokens,
                    stop_reason = request.StopReason,
                };

                builder.Append(JsonSerializer.Serialize(row, JsonOptions.Compact)).Append('\n');
            }
        }

        Files.WriteText(path, builder.ToString());
    }

    internal static void WriteSummary(string runId, IReadOnlyList<RunSummary> summaries)
    {
        var path = Path.Combine(Paths.RunDirectory(runId), "summary.csv");
        var builder = new StringBuilder();

        builder.AppendLine(string.Join(',',
            "run_id", "arm", "slice", "keys", "rep", "input", "output", "cacheCreation", "cacheRead", "total",
            "requests", "wall_clock_s", "validation", "approx_cost_usd", "mcp_tool_calls", "mcp_tool_input_bytes",
            "mcp_tool_result_bytes", "all_tool_calls", "num_turns", "exit_code", "timed_out",
            "stream_vs_result_delta_pct", "cross_check", "translated_values", "tool_mix", "failure_reason"));

        foreach (var summary in summaries)
        {
            builder.AppendLine(string.Join(',',
                Csv(summary.RunId), Csv(summary.Arm), Csv(summary.Slice), summary.Keys, summary.Rep,
                summary.InputTokens, summary.OutputTokens, summary.CacheCreationTokens, summary.CacheReadTokens,
                summary.TotalTokens, summary.Requests,
                summary.WallClockSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                Csv(summary.Validation),
                summary.ApproximateCostUsd.ToString("0.####", CultureInfo.InvariantCulture),
                summary.McpToolCalls, summary.McpToolInputBytes, summary.McpToolResultBytes, summary.AllToolCalls,
                summary.NumTurns, summary.ExitCode, summary.TimedOut ? "true" : "false",
                summary.StreamVersusResultDeltaPercent.ToString("0.##", CultureInfo.InvariantCulture),
                Csv(summary.CrossCheck), summary.TranslatedValues, Csv(summary.ToolMix), Csv(summary.FailureReason)));
        }

        Files.WriteText(path, builder.ToString());
    }

    private static string Csv(string value)
    {
        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);

        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}
