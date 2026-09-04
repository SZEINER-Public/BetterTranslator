using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed record NaturalnessBand(
    [property: JsonPropertyName("limit")] double Limit,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("minimumTokens")] int MinimumTokens)
{
    [JsonIgnore]
    public bool Calibrated => Samples > 0;
}

public sealed record NaturalnessPairProfile(
    [property: JsonPropertyName("pair")] string Pair,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("crossing")] NaturalnessBand Crossing,
    [property: JsonPropertyName("tagDivergence")] NaturalnessBand TagDivergence,
    [property: JsonPropertyName("sentenceExcess")] NaturalnessBand SentenceExcess,
    [property: JsonPropertyName("tagReference")] IReadOnlyDictionary<string, int> TagReference);

public sealed class NaturalnessProfile
{
    public const string ResourceName = "BetterTranslator.Core.Verification.Checks.Naturalness.naturalness-profile.json";

    public const string UnitSentence = "sentence";

    public const string UnitBlock = "block";

    private static readonly Lazy<NaturalnessProfile> Shipped = new(LoadShipped);

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("improvementMargin")]
    public int ImprovementMargin { get; init; } = 1;

    [JsonPropertyName("meaningTolerance")]
    public double MeaningTolerance { get; init; } = 0.05;

    [JsonPropertyName("pairs")]
    public IReadOnlyList<NaturalnessPairProfile> Pairs { get; init; } = [];

    public static NaturalnessProfile Default => Shipped.Value;

    public NaturalnessPairProfile? For(string sourceLanguage, string targetLanguage, string unit)
    {
        var pair = Key(sourceLanguage, targetLanguage);

        return Pairs.FirstOrDefault(p => string.Equals(p.Pair, pair, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Unit, unit, StringComparison.OrdinalIgnoreCase));
    }

    public static string Key(string sourceLanguage, string targetLanguage) => sourceLanguage.ToLowerInvariant() + "-" + targetLanguage.ToLowerInvariant();

    public static NaturalnessProfile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<NaturalnessProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new NaturalnessProfile();
    }

    public string Render() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static NaturalnessProfile LoadShipped()
    {
        using var stream = typeof(NaturalnessProfile).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("naturalness profile resource missing: " + ResourceName);
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }
}
