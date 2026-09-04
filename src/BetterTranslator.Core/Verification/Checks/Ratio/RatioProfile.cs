using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Core.Verification.Checks.Ratio;

public static class RatioUnitType
{
    public const string Word = "word";

    public const string Sentence = "sentence";

    public const string Block = "block";

    public const string Value = "value";

    public const string Cue = "cue";
}

public sealed record RatioBand(
    [property: JsonPropertyName("low")] double Low,
    [property: JsonPropertyName("high")] double High,
    [property: JsonPropertyName("median")] double Median,
    [property: JsonPropertyName("samples")] int Samples);

public sealed record RatioLimit(
    [property: JsonPropertyName("limit")] double Limit,
    [property: JsonPropertyName("observedMax")] double ObservedMax,
    [property: JsonPropertyName("samples")] int Samples);

public sealed record RatioBandSet(
    [property: JsonPropertyName("pair")] string Pair,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("lengthRatio")] RatioBand LengthRatio,
    [property: JsonPropertyName("repetitionRun")] RatioLimit RepetitionRun,
    [property: JsonPropertyName("compressionRatio")] RatioLimit CompressionRatio,
    [property: JsonPropertyName("sentenceExcess")] RatioLimit SentenceExcess);

public sealed record RatioCheckCalibration(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("precision")] double Precision,
    [property: JsonPropertyName("recall")] double Recall,
    [property: JsonPropertyName("heldOutPositives")] int HeldOutPositives,
    [property: JsonPropertyName("heldOutNegatives")] int HeldOutNegatives,
    [property: JsonPropertyName("truePositives")] int TruePositives,
    [property: JsonPropertyName("falsePositives")] int FalsePositives,
    [property: JsonPropertyName("scoreWeight")] double ScoreWeight);

public sealed record RatioSource(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("pairs")] int Pairs);

public sealed record RatioProfile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("precisionFloor")] double PrecisionFloor,
    [property: JsonPropertyName("bandQuantile")] double BandQuantile,
    [property: JsonPropertyName("heldOutEvery")] int HeldOutEvery,
    [property: JsonPropertyName("negativeRecipe")] string NegativeRecipe,
    [property: JsonPropertyName("sources")] IReadOnlyList<RatioSource> Sources,
    [property: JsonPropertyName("bands")] IReadOnlyList<RatioBandSet> Bands,
    [property: JsonPropertyName("checks")] IReadOnlyList<RatioCheckCalibration> Checks)
{
    public const string ResourceName = "BetterTranslator.Core.Verification.Checks.Ratio.ratio-profile.json";

    private static readonly Lazy<RatioProfile> Embedded = new(LoadEmbedded);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static RatioProfile Default => Embedded.Value;

    public RatioBandSet? BandFor(string pair, string unit) =>
        Bands.FirstOrDefault(b =>
            string.Equals(b.Pair, pair, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Unit, unit, StringComparison.OrdinalIgnoreCase));

    public RatioCheckCalibration? CheckFor(string checkId) =>
        Checks.FirstOrDefault(c => string.Equals(c.Id, checkId, StringComparison.Ordinal));

    public bool IsEnabled(string checkId) => CheckFor(checkId)?.Enabled ?? false;

    public static RatioProfile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<RatioProfile>(json, Options)
            ?? throw new InvalidDataException("ratio profile is empty");
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options) + "\n";

    private static RatioProfile LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("ratio profile resource is missing");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }
}
