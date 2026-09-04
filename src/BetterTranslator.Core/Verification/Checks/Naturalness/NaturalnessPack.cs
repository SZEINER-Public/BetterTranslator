using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed record CliticGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("forms")] IReadOnlyList<string> Forms);

public sealed record TwoWordConditional(
    [property: JsonPropertyName("particle")] string Particle,
    [property: JsonPropertyName("auxiliaries")] IReadOnlyList<string> Auxiliaries,
    [property: JsonPropertyName("contractedForms")] IReadOnlyList<string> ContractedForms);

public sealed record NaturalnessRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("decidable")] bool Decidable,
    [property: JsonPropertyName("evidence")] string Evidence)
{
    public bool NeedsTags => Signal.Contains("tags", StringComparison.Ordinal);

    public CheckSeverity CheckSeverity => Enum.TryParse<CheckSeverity>(Severity, true, out var parsed) ? parsed : CheckSeverity.Score;
}

public sealed class NaturalnessPack
{
    public const string SignalAlignment = "alignment";

    public const string SignalTags = "tags";

    public const string SignalInventory = "inventory";

    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("analyzer")]
    public string Analyzer { get; init; } = string.Empty;

    [JsonPropertyName("proDrop")]
    public bool ProDrop { get; init; }

    [JsonPropertyName("alphabet")]
    public string Alphabet { get; init; } = string.Empty;

    [JsonPropertyName("foreignForms")]
    public IReadOnlyList<string> ForeignForms { get; init; } = [];

    [JsonPropertyName("cliticGroups")]
    public IReadOnlyList<CliticGroup> CliticGroups { get; init; } = [];

    [JsonPropertyName("clauseBreakers")]
    public IReadOnlyList<string> ClauseBreakers { get; init; } = [];

    [JsonPropertyName("cliticHomographs")]
    public IReadOnlyList<string> CliticHomographs { get; init; } = [];

    [JsonPropertyName("subjectPronouns")]
    public IReadOnlyList<string> SubjectPronouns { get; init; } = [];

    [JsonPropertyName("possessives")]
    public IReadOnlyList<string> Possessives { get; init; } = [];

    [JsonPropertyName("reflexivePossessive")]
    public string ReflexivePossessive { get; init; } = string.Empty;

    [JsonPropertyName("lightVerbs")]
    public IReadOnlyList<string> LightVerbs { get; init; } = [];

    [JsonPropertyName("deverbalSuffixes")]
    public IReadOnlyList<string> DeverbalSuffixes { get; init; } = [];

    [JsonPropertyName("passiveAuxiliaries")]
    public IReadOnlyList<string> PassiveAuxiliaries { get; init; } = [];

    [JsonPropertyName("quotationOpen")]
    public string QuotationOpen { get; init; } = string.Empty;

    [JsonPropertyName("quotationClose")]
    public string QuotationClose { get; init; } = string.Empty;

    [JsonPropertyName("foreignQuotationPairs")]
    public IReadOnlyList<IReadOnlyList<string>> ForeignQuotationPairs { get; init; } = [];

    [JsonPropertyName("pluralMarkers")]
    public IReadOnlyList<string> PluralMarkers { get; init; } = [];

    [JsonPropertyName("twoWordConditional")]
    public TwoWordConditional? TwoWordConditional { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("rules")]
    public IReadOnlyList<NaturalnessRule> Rules { get; init; } = [];

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public NaturalnessRule? Rule(string id) => Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    public string Tag(string key) => Tags.TryGetValue(key, out var tag) ? tag : string.Empty;

    public IReadOnlyList<string> AllClitics => [.. CliticGroups.SelectMany(g => g.Forms)];

    public int CliticGroupIndex(string form)
    {
        for (var i = 0; i < CliticGroups.Count; i++)
        {
            if (CliticGroups[i].Forms.Contains(form, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed record PackLoadResult(string Language, NaturalnessPack? Pack, string Reason)
{
    public bool Loaded => Pack is not null;
}

public static class NaturalnessPackLoader
{
    public const string ResourcePrefix = "BetterTranslator.Core.Verification.Checks.Naturalness.Packs.nat-";

    public const string ResourceSuffix = ".json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<IReadOnlyDictionary<string, PackLoadResult>> Shipped = new(LoadShipped);

    public static IReadOnlyDictionary<string, PackLoadResult> ShippedPacks => Shipped.Value;

    public static PackLoadResult Parse(string language, string json)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(json);

        NaturalnessPack? pack;

        try
        {
            pack = JsonSerializer.Deserialize<NaturalnessPack>(json, Options);
        }
        catch (JsonException ex)
        {
            return new PackLoadResult(language, null, "pack is not valid json: " + ex.Message);
        }

        if (pack is null)
        {
            return new PackLoadResult(language, null, "pack is empty");
        }

        var reason = Validate(pack);

        return reason is null ? new PackLoadResult(pack.Language, pack, string.Empty) : new PackLoadResult(language, null, reason);
    }

    public static string? Validate(NaturalnessPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (string.IsNullOrWhiteSpace(pack.Language))
        {
            return "pack declares no language";
        }

        if (pack.Rules.Count == 0)
        {
            return "pack declares no rules";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in pack.Rules)
        {
            if (!rule.Id.StartsWith(CheckId.Naturalness.Category + "-", StringComparison.Ordinal))
            {
                return "rule '" + rule.Id + "' is outside the naturalness catalogue";
            }

            if (!ids.Add(rule.Id))
            {
                return "rule '" + rule.Id + "' is declared twice";
            }

            if (rule.Decidable && string.IsNullOrWhiteSpace(rule.Signal))
            {
                return "decidable rule '" + rule.Id + "' names no signal";
            }

            if (string.IsNullOrWhiteSpace(rule.Evidence))
            {
                return "rule '" + rule.Id + "' has no evidence template";
            }
        }

        var foreign = new HashSet<string>(pack.ForeignForms, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var form in pack.AllClitics.Concat(pack.SubjectPronouns).Concat(pack.LightVerbs).Concat(pack.Possessives).Concat(pack.PassiveAuxiliaries))
        {
            if (foreign.Contains(form))
            {
                return "form '" + form + "' belongs to another language according to the pack's own foreign form list";
            }

            if (pack.Alphabet.Length > 0 && form.Any(c => char.IsLetter(c) && !pack.Alphabet.Contains(char.ToLowerInvariant(c))))
            {
                return "form '" + form + "' uses a letter outside the pack alphabet";
            }
        }

        foreach (var group in pack.CliticGroups)
        {
            if (group.Forms.Count == 0)
            {
                return "clitic group '" + group.Name + "' is empty";
            }
        }

        if (pack.TwoWordConditional is { } conditional)
        {
            if (conditional.ContractedForms.Any(f => !foreign.Contains(f)))
            {
                return "two-word conditional lists a contracted form that is not marked foreign";
            }

            if (pack.AllClitics.Any(f => conditional.ContractedForms.Contains(f, StringComparer.OrdinalIgnoreCase)))
            {
                return "clitic inventory carries a contracted conditional the pack forbids";
            }
        }

        return null;
    }

    public static IReadOnlyDictionary<string, PackLoadResult> LoadShipped()
    {
        var assembly = typeof(NaturalnessPackLoader).Assembly;
        var results = new Dictionary<string, PackLoadResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var language = name[ResourcePrefix.Length..^ResourceSuffix.Length];

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);

            var result = Parse(language, reader.ReadToEnd());

            results[language] = result.Loaded && !string.Equals(result.Language, language, StringComparison.OrdinalIgnoreCase)
                ? new PackLoadResult(language, null, "pack resource '" + language + "' declares language '" + result.Language + "'")
                : result;
        }

        return results;
    }
}
