namespace BetterTranslator.Engine.Languages;

public enum LanguageAvailability
{
    Supported,
    Unverified,
    Unsupported,
}

public sealed record LanguageListing(
    string Code,
    string Name,
    string Endonym,
    string Script,
    string Direction,
    LanguageAvailability Availability,
    string Reason,
    LanguageAsset Asset)
{
    public bool IsAvailable => Availability != LanguageAvailability.Unsupported;
}

public sealed class LanguageCatalog
{
    private readonly LanguageRegistry _registry;
    private readonly ModelLanguages _models;
    private readonly LanguageAssets _assets;

    public LanguageCatalog()
        : this(new LanguageRegistry(), new ModelLanguages(), new LanguageAssets())
    {
    }

    public LanguageCatalog(LanguageRegistry registry, ModelLanguages models, LanguageAssets assets)
    {
        _registry = registry;
        _models = models;
        _assets = assets;
    }

    public LanguageRegistry Registry => _registry;

    public LanguageAssets Assets => _assets;

    public ModelLanguages Models => _models;

    public IReadOnlyList<LanguageListing> For(string? modelId)
    {
        var family = _models.For(modelId);
        var codes = family is { HasSource: true } ? family.Codes : null;

        return [.. _registry.All.Select(entry => Listing(entry, family, codes))];
    }

    public IReadOnlyList<LanguageListing> AvailableFor(string? modelId) =>
        [.. For(modelId).Where(l => l.IsAvailable)];

    public IReadOnlyList<LanguageListing> UnavailableFor(string? modelId) =>
        [.. For(modelId).Where(l => !l.IsAvailable)];

    private LanguageListing Listing(
        LanguageEntry entry,
        ModelLanguageFamily? family,
        IReadOnlySet<string>? codes)
    {
        var (availability, reason) = Verdict(entry, family, codes);

        return new LanguageListing(
            entry.Code,
            entry.Name,
            entry.Native,
            entry.Script,
            entry.Dir,
            availability,
            reason,
            _assets.For(entry));
    }

    private static (LanguageAvailability Availability, string Reason) Verdict(
        LanguageEntry entry,
        ModelLanguageFamily? family,
        IReadOnlySet<string>? codes)
    {
        if (family is null)
        {
            return (LanguageAvailability.Unverified, "No language list is published for this model.");
        }

        if (codes is null)
        {
            return (LanguageAvailability.Unverified, Sentence(family.Provenance));
        }

        return codes.Contains(entry.Code)
            ? (LanguageAvailability.Supported, $"Listed in {family.Provenance}.")
            : (LanguageAvailability.Unsupported, $"Not listed in {family.Provenance}.");
    }

    private static string Sentence(string provenance) =>
        provenance.Length == 0
            ? "No language list is published for this model."
            : $"Support is unverified: {provenance}.";
}
