using System.Text.Json.Serialization;

namespace BetterTranslator.Engine.Languages;

public sealed record LanguageToolRow
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("endonym")]
    public required string Endonym { get; init; }

    [JsonPropertyName("script")]
    public required string Script { get; init; }

    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    [JsonPropertyName("availability")]
    public required string Availability { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("asset")]
    public required string Asset { get; init; }
}

public static class LanguageTools
{
    public const string ListLanguages = "list_languages";

    public static IReadOnlyList<LanguageToolRow> List(string? modelId) =>
        List(new LanguageCatalog(), modelId);

    public static IReadOnlyList<LanguageToolRow> List(LanguageCatalog catalog, string? modelId) =>
        [.. catalog.For(modelId).Select(Row)];

    private static LanguageToolRow Row(LanguageListing listing) => new()
    {
        Code = listing.Code,
        Name = listing.Name,
        Endonym = listing.Endonym,
        Script = listing.Script,
        Direction = listing.Direction,
        Availability = listing.Availability switch
        {
            LanguageAvailability.Supported => "supported",
            LanguageAvailability.Unsupported => "unsupported",
            _ => "unverified",
        },
        Reason = listing.Reason,
        Asset = listing.Asset.Kind == LanguageAssetKind.Flag ? "flag" : "badge",
    };
}
