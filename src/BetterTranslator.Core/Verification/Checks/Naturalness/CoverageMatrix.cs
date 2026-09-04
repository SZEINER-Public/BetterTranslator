using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed record CoverageEntry(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("models")] IReadOnlyList<string> Models,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("analyzer")] string Analyzer,
    [property: JsonPropertyName("analyzerInRepository")] bool AnalyzerInRepository,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("mechanism")] string? Mechanism)
{
    public bool Implemented => string.Equals(Status, CoverageMatrix.StatusImplemented, StringComparison.Ordinal);

    public bool Unclassified => string.Equals(Status, CoverageMatrix.StatusUnclassified, StringComparison.Ordinal);
}

public sealed record CoverageGroup(
    [property: JsonPropertyName("mechanism")] string Mechanism,
    [property: JsonPropertyName("ruleKinds")] IReadOnlyList<string> RuleKinds);

public sealed class CoverageMatrix
{
    public const string ResourceName = "BetterTranslator.Core.Verification.Checks.Naturalness.coverage-matrix.json";

    public const string StatusImplemented = "implemented";

    public const string StatusDeclared = "declared";

    public const string StatusUnclassified = "unclassified";

    private static readonly Lazy<CoverageMatrix> Shipped = new(LoadShipped);

    [JsonPropertyName("groups")]
    public IReadOnlyDictionary<string, CoverageGroup> Groups { get; init; } = new Dictionary<string, CoverageGroup>(StringComparer.Ordinal);

    [JsonPropertyName("languages")]
    public IReadOnlyList<CoverageEntry> Languages { get; init; } = [];

    public static CoverageMatrix Default => Shipped.Value;

    public CoverageEntry? Find(string code) =>
        Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> RuleKindsFor(string code)
    {
        var entry = Find(code);

        return entry?.Group is { } group && Groups.TryGetValue(group, out var g) ? g.RuleKinds : [];
    }

    public string? Validate()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Languages)
        {
            if (!codes.Add(entry.Code))
            {
                return "language '" + entry.Code + "' appears twice";
            }

            if (entry.Status is not (StatusImplemented or StatusDeclared or StatusUnclassified))
            {
                return "language '" + entry.Code + "' has status '" + entry.Status + "'";
            }

            if (entry.Unclassified && entry.Group is not null)
            {
                return "language '" + entry.Code + "' is unclassified but names a group";
            }

            if (entry.Unclassified && string.IsNullOrWhiteSpace(entry.Mechanism))
            {
                return "language '" + entry.Code + "' is unclassified without the mechanism that keeps it out";
            }

            if (!entry.Unclassified && (entry.Group is null || !Groups.ContainsKey(entry.Group)))
            {
                return "language '" + entry.Code + "' names an unknown group";
            }

            if (entry.Models.Count == 0)
            {
                return "language '" + entry.Code + "' is offered by no model";
            }
        }

        return null;
    }

    public static CoverageMatrix Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<CoverageMatrix>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CoverageMatrix();
    }

    private static CoverageMatrix LoadShipped()
    {
        using var stream = typeof(CoverageMatrix).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("coverage matrix resource missing: " + ResourceName);
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }
}
