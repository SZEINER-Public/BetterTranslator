using BetterTranslator.Engine.Languages;

namespace BetterTranslator.App.ViewModels;

public sealed record TargetLanguage(
    string Name,
    string NativeName,
    string Code,
    string Script,
    string Direction,
    string FlagKey,
    string CodeLabel,
    LanguageAvailability Availability,
    string Reason)
{
    public bool HasFlag => FlagKey.Length > 0;

    public bool IsUnknown => Code.Length == 0;

    public bool IsAvailable => Availability != LanguageAvailability.Unsupported;

    public bool IsUnavailable => !IsAvailable;

    /// <summary>
    /// Diacritics are ignored on both sides, so "Cestina" finds Čeština and
    /// "Espanol" finds Español. Half the native names in the registry carry
    /// marks that are awkward or impossible to type on the keyboard the reader
    /// is holding, and a search that demanded them would be a search that only
    /// worked for languages written in plain ASCII.
    ///
    /// Invariant rather than current culture: which languages a query finds must
    /// not depend on the machine's regional settings.
    /// </summary>
    private static readonly System.Globalization.CompareInfo Compare =
        System.Globalization.CultureInfo.InvariantCulture.CompareInfo;

    private const System.Globalization.CompareOptions Loose =
        System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;

    /// <summary>Search matches the English name, the native name or the code.</summary>
    public bool Matches(string? query)
    {
        var q = query?.Trim();

        if (string.IsNullOrEmpty(q))
        {
            return true;
        }

        return Compare.IndexOf(Name, q, Loose) >= 0
            || Compare.IndexOf(NativeName, q, Loose) >= 0
            || Code.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }

    public static TargetLanguage From(LanguageListing listing) =>
        new(listing.Name,
            listing.Endonym,
            listing.Code,
            listing.Script,
            listing.Direction,
            listing.Asset.IsFlag ? listing.Asset.Key : string.Empty,
            listing.Asset.Badge,
            listing.Availability,
            listing.Reason);

    private static readonly LanguageCatalog Catalogue = new();

    public static IReadOnlyList<TargetLanguage> All { get; } =
        [.. Catalogue.For(null).Select(From)];

    public static IReadOnlyList<TargetLanguage> ForCatalog(string? modelId) =>
        [.. Catalogue.For(modelId).Select(From)];

    public static IReadOnlyList<TargetLanguage> ForModel(string? modelId) =>
        [.. Catalogue.For(modelId).Where(l => l.IsAvailable).Select(From)];

    /// <summary>
    /// What text is assumed to be written in. There is no source picker yet, so
    /// everything is treated as English on the way in.
    /// </summary>
    public static TargetLanguage Source { get; } =
        All.FirstOrDefault(l => l.Code == "en")
        ?? new("English", "English", "en", "Latn", "ltr", string.Empty, "EN", LanguageAvailability.Unverified, string.Empty);

    /// <summary>The one the composer starts on when nothing is stored.</summary>
    public static TargetLanguage Default { get; } =
        All.FirstOrDefault(l => l.Code == "cs") ?? Source;

    /// <summary>
    /// No language decided. Detection resolves to this rather than to a guess,
    /// and the guard refuses a pair that carries it rather than filling it in
    /// from the other side.
    /// </summary>
    public static TargetLanguage Unknown { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, "ltr", string.Empty, "?",
            LanguageAvailability.Unverified, string.Empty);
}
