using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterTranslator.Bench;

internal sealed record ApiRequestRow(
    int Index,
    string MessageId,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    string StopReason);

internal sealed record ToolEventRow(
    string ToolUseId,
    string ToolName,
    long ToolInputSizeBytes,
    long ToolResultSizeBytes,
    bool Success,
    string ResultExcerpt);

internal sealed record ModelUsageRow(string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens, double CostUsd);

internal sealed record PrintResult(
    bool Present,
    bool IsError,
    string Subtype,
    int NumTurns,
    double TotalCostUsd,
    long DurationMs,
    long DurationApiMs,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    string ResultText,
    long ApiErrorStatus,
    string TerminalReason,
    IReadOnlyList<ModelUsageRow> ModelUsage);

internal sealed record ParsedLog(
    IReadOnlyList<ApiRequestRow> Requests,
    IReadOnlyList<ToolEventRow> Tools,
    PrintResult Result,
    int UnparsableLines);

internal static class StreamLog
{
    internal static ParsedLog Parse(string path)
    {
        var requests = new List<ApiRequestRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, (string Name, long Bytes)>(StringComparer.Ordinal);
        var tools = new List<ToolEventRow>();
        var result = Missing();
        var unparsable = 0;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            JsonNode? node;

            try
            {
                node = JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                unparsable++;
                continue;
            }

            var type = node?["type"]?.GetValue<string>();

            switch (type)
            {
                case "assistant":
                    ReadAssistant(node!, requests, seen, pending);
                    break;
                case "user":
                    ReadToolResults(node!, pending, tools);
                    break;
                case "result":
                    result = ReadResult(node!);
                    break;
            }
        }

        foreach (var (id, value) in pending)
        {
            tools.Add(new ToolEventRow(id, value.Name, value.Bytes, 0, Success: false, ResultExcerpt: string.Empty));
        }

        return new ParsedLog(requests, tools, result, unparsable);
    }

    private static void ReadAssistant(
        JsonNode node,
        List<ApiRequestRow> requests,
        HashSet<string> seen,
        Dictionary<string, (string Name, long Bytes)> pending)
    {
        var message = node["message"];

        if (message is null)
        {
            return;
        }

        var id = message["id"]?.GetValue<string>() ?? string.Empty;
        var usage = message["usage"];

        if (id.Length > 0 && usage is not null && seen.Add(id))
        {
            requests.Add(new ApiRequestRow(
                Index: requests.Count + 1,
                MessageId: id,
                Model: message["model"]?.GetValue<string>() ?? string.Empty,
                InputTokens: Number(usage["input_tokens"]),
                OutputTokens: Number(usage["output_tokens"]),
                CacheCreationTokens: Number(usage["cache_creation_input_tokens"]),
                CacheReadTokens: Number(usage["cache_read_input_tokens"]),
                StopReason: message["stop_reason"]?.GetValue<string>() ?? string.Empty));
        }

        foreach (var block in message["content"]?.AsArray() ?? [])
        {
            if (block?["type"]?.GetValue<string>() != "tool_use")
            {
                continue;
            }

            var toolUseId = block["id"]?.GetValue<string>() ?? string.Empty;
            var name = block["name"]?.GetValue<string>() ?? string.Empty;
            var input = block["input"]?.ToJsonString(JsonOptions.Compact) ?? string.Empty;

            if (toolUseId.Length > 0)
            {
                pending[toolUseId] = (name, Encoding.UTF8.GetByteCount(input));
            }
        }
    }

    private static void ReadToolResults(
        JsonNode node,
        Dictionary<string, (string Name, long Bytes)> pending,
        List<ToolEventRow> tools)
    {
        foreach (var block in node["message"]?["content"]?.AsArray() ?? [])
        {
            if (block?["type"]?.GetValue<string>() != "tool_result")
            {
                continue;
            }

            var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? string.Empty;
            var content = block["content"];
            var text = content is null ? string.Empty : TextOf(content);
            var bytes = Encoding.UTF8.GetByteCount(text);
            var success = !(block["is_error"]?.GetValue<bool>() ?? false);
            var excerpt = Excerpt(text);

            if (pending.Remove(toolUseId, out var started))
            {
                tools.Add(new ToolEventRow(toolUseId, started.Name, started.Bytes, bytes, success, excerpt));
            }
            else
            {
                tools.Add(new ToolEventRow(toolUseId, "unmatched", 0, bytes, success, excerpt));
            }
        }
    }

    private static string TextOf(JsonNode content)
    {
        if (content is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }

        if (content is JsonArray array)
        {
            var builder = new StringBuilder();

            foreach (var item in array)
            {
                var text = item?["text"]?.GetValue<string>();
                builder.Append(text ?? item?.ToJsonString(JsonOptions.Compact) ?? string.Empty);
            }

            return builder.ToString();
        }

        return content.ToJsonString(JsonOptions.Compact);
    }

    private static PrintResult ReadResult(JsonNode node)
    {
        var usage = node["usage"];
        var models = new List<ModelUsageRow>();

        foreach (var entry in node["modelUsage"]?.AsObject() ?? [])
        {
            models.Add(new ModelUsageRow(
                Model: entry.Key,
                InputTokens: Number(entry.Value?["inputTokens"]),
                OutputTokens: Number(entry.Value?["outputTokens"]),
                CacheReadTokens: Number(entry.Value?["cacheReadInputTokens"]),
                CacheCreationTokens: Number(entry.Value?["cacheCreationInputTokens"]),
                CostUsd: Decimal(entry.Value?["costUSD"])));
        }

        return new PrintResult(
            Present: true,
            IsError: node["is_error"]?.GetValue<bool>() ?? false,
            Subtype: node["subtype"]?.GetValue<string>() ?? string.Empty,
            NumTurns: (int)Number(node["num_turns"]),
            TotalCostUsd: Decimal(node["total_cost_usd"]),
            DurationMs: Number(node["duration_ms"]),
            DurationApiMs: Number(node["duration_api_ms"]),
            InputTokens: Number(usage?["input_tokens"]),
            OutputTokens: Number(usage?["output_tokens"]),
            CacheCreationTokens: Number(usage?["cache_creation_input_tokens"]),
            CacheReadTokens: Number(usage?["cache_read_input_tokens"]),
            ResultText: node["result"]?.ToJsonString(JsonOptions.Compact) ?? string.Empty,
            ApiErrorStatus: Number(node["api_error_status"]),
            TerminalReason: node["terminal_reason"]?.GetValue<string>() ?? string.Empty,
            ModelUsage: models);
    }

    private static PrintResult Missing() => new(false, true, "missing", 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, 0, string.Empty, []);

    private static string Excerpt(string text)
    {
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();

        return flattened.Length <= 240 ? flattened : flattened[..237] + "...";
    }

    private static long Number(JsonNode? node)
    {
        if (node is null)
        {
            return 0;
        }

        return node.GetValueKind() == JsonValueKind.Number ? (long)node.GetValue<double>() : 0;
    }

    private static double Decimal(JsonNode? node)
    {
        if (node is null)
        {
            return 0;
        }

        return node.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : 0;
    }
}
