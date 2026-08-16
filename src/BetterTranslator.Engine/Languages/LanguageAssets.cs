using System.Text.Json.Serialization;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Languages;

public enum LanguageAssetKind
{
    Flag,
    Badge,
}

public sealed record LanguageAsset(LanguageAssetKind Kind, string Key, string Badge)
{
    public bool IsFlag => Kind == LanguageAssetKind.Flag && Key.Length > 0;
}

internal sealed record LanguageAssetRow
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

internal sealed record LanguageAssetDocument
{
    [JsonPropertyName("assets")]
    public IReadOnlyList<LanguageAssetRow> Assets { get; init; } = [];
}

public sealed class LanguageAssets
{
    private const string ResourceName = "BetterTranslator.Engine.Data.language-assets.json";

    private static readonly Lazy<LanguageAssets> Shipped = new(() => new LanguageAssets(LoadEmbedded()));

    private readonly IReadOnlyDictionary<string, LanguageAsset> _byCode;

    public LanguageAssets()
        : this(Shipped.Value._byCode)
    {
    }

    public LanguageAssets(IReadOnlyDictionary<string, LanguageAsset> byCode) => _byCode = byCode;

    public IReadOnlyCollection<string> MappedCodes => [.. _byCode.Keys];

    public IReadOnlyCollection<string> FlagKeys =>
        [.. _byCode.Values.Where(a => a.IsFlag).Select(a => a.Key)];

    public LanguageAsset For(LanguageEntry entry) => For(entry.Code);

    public LanguageAsset For(string code)
    {
        var badge = Badge(code);

        if (!_byCode.TryGetValue(code, out var asset))
        {
            return new LanguageAsset(LanguageAssetKind.Badge, string.Empty, badge);
        }

        return asset.Kind == LanguageAssetKind.Flag && asset.Key.Length > 0
            ? asset
            : asset with { Kind = LanguageAssetKind.Badge, Key = string.Empty, Badge = badge };
    }

    public bool Declares(string code) => _byCode.ContainsKey(code);

    private static string Badge(string code) =>
        code.Length == 0 ? "?" : code.ToUpperInvariant();

    private static IReadOnlyDictionary<string, LanguageAsset> LoadEmbedded()
    {
        var document = BomSafeJson.Deserialize<LanguageAssetDocument>(Resource());
        var table = new Dictionary<string, LanguageAsset>(StringComparer.Ordinal);

        foreach (var row in document?.Assets ?? [])
        {
            if (row.Code.Length == 0 || table.ContainsKey(row.Code))
            {
                continue;
            }

            var flag = string.Equals(row.Kind, "flag", StringComparison.Ordinal) && !string.IsNullOrEmpty(row.Key);

            table[row.Code] = flag
                ? new LanguageAsset(LanguageAssetKind.Flag, row.Key!, Badge(row.Code))
                : new LanguageAsset(LanguageAssetKind.Badge, string.Empty, Badge(row.Code));
        }

        return table;
    }

    private static byte[] Resource()
    {
        using var stream = typeof(LanguageAssets).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
