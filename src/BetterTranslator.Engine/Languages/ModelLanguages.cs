using System.Text.Json.Serialization;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Languages;

public sealed record ModelLanguageFamily
{
    [JsonPropertyName("family")]
    public string Family { get; init; } = string.Empty;

    [JsonPropertyName("match")]
    public string Match { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("provenance")]
    public string Provenance { get; init; } = string.Empty;

    [JsonPropertyName("observed")]
    public string Observed { get; init; } = string.Empty;

    [JsonPropertyName("sourced")]
    public IReadOnlyList<string> Sourced { get; init; } = [];

    [JsonPropertyName("resolves")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Resolves { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public bool HasSource => Sourced.Count > 0;

    public IReadOnlySet<string> Codes
    {
        get
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourced in Sourced)
            {
                if (Resolves.TryGetValue(sourced, out var mapped))
                {
                    foreach (var code in mapped)
                    {
                        codes.Add(code);
                    }

                    continue;
                }

                codes.Add(sourced);
            }

            return codes;
        }
    }
}

internal sealed record ModelLanguageDocument
{
    [JsonPropertyName("families")]
    public IReadOnlyList<ModelLanguageFamily> Families { get; init; } = [];
}

public sealed class ModelLanguages
{
    private const string ResourceName = "BetterTranslator.Engine.Data.model-languages.json";

    private static readonly Lazy<IReadOnlyList<ModelLanguageFamily>> Embedded = new(LoadEmbedded);

    private readonly IReadOnlyList<ModelLanguageFamily> _families;

    public ModelLanguages()
        : this(Embedded.Value)
    {
    }

    public ModelLanguages(IReadOnlyList<ModelLanguageFamily> families) => _families = families;

    public IReadOnlyList<ModelLanguageFamily> All => _families;

    public ModelLanguageFamily? For(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        return _families.FirstOrDefault(f =>
            f.Match.Length > 0 && modelId.Contains(f.Match, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ModelLanguageFamily> LoadEmbedded()
    {
        using var stream = typeof(ModelLanguages).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return BomSafeJson.Deserialize<ModelLanguageDocument>(buffer.ToArray())?.Families ?? [];
    }
}
