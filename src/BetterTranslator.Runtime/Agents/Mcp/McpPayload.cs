using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace BetterTranslator.Runtime.Agents.Mcp;

public static class McpPayload
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CallToolResult Ok(object payload, string markdown) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(payload, Json),
        Content = [new TextContentBlock { Text = markdown }],
    };

    public static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    public const string VerificationPlaceholder = "\"@@verification@@\"";

    public static JsonElement Schema(string json) => JsonDocument.Parse(json.Replace(VerificationPlaceholder, VerificationSummary.SchemaObject, StringComparison.Ordinal)).RootElement.Clone();

    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var markdown = new StringBuilder();

        markdown.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        markdown.Append('|').Append(string.Join("|", headers.Select(_ => "---"))).AppendLine("|");

        foreach (var row in rows)
        {
            markdown.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
        }

        return markdown.ToString().TrimEnd();
    }

    public static string Pairs(IReadOnlyList<(string Field, string Value)> pairs) =>
        Table(["field", "value"], pairs.Select(p => (IReadOnlyList<string>)[p.Field, p.Value]));

    public const int CellLimit = 2000;

    public const int FieldLimit = 20000;

    public static string Clip(string? value, int limit)
    {
        var text = value ?? string.Empty;

        return text.Length <= limit
            ? text
            : text[..limit] + $" [clipped, {text.Length} characters in all]";
    }

    private static string Cell(string? value) =>
        Clip(value, CellLimit).Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
