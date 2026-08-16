using System.Reflection;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Languages;

/// <summary>
/// The target-language registry, and resolving a name to it. Ported from
/// `scripts\languages.ps1`.
///
/// One list, read by everything that takes a language. Adding a language is a
/// data edit.
///
/// Two things it exists to prevent, both carried across from the original:
///
/// A CODE THAT LOOKS RIGHT AND IS NOT. Ukrainian is "uk" in ISO 639-1; "ua" is
/// the country code, and a locale file written as ua.json is not found by
/// tooling expecting uk. Aliases resolve to the canonical code so both work and
/// only one is ever written.
///
/// A LANGUAGE THE LOADED MODEL CANNOT DO. Every gate in this engine compares
/// structure, placeholders and terminology -- never meaning. Asked for Thai, a
/// model never trained on Thai returns something structurally perfect that no
/// check can fault, and the run reports success. The registry marks which
/// languages a model was trained on so the caller can say so out loud
/// beforehand. It warns rather than refuses: the point of a model-agnostic
/// engine is that a better model may be loaded tomorrow.
/// </summary>
public sealed class LanguageRegistry
{
    private const string ResourceName = "BetterTranslator.Engine.Data.languages.json";

    private static readonly Lazy<IReadOnlyList<LanguageEntry>> Embedded = new(LoadEmbedded);

    private readonly IReadOnlyList<LanguageEntry> _all;

    /// <summary>The registry as shipped.</summary>
    public LanguageRegistry()
        : this(Embedded.Value)
    {
    }

    /// <summary>Overridable, so a test can drive a registry it wrote itself.</summary>
    public LanguageRegistry(IReadOnlyList<LanguageEntry> entries) => _all = entries;

    public IReadOnlyList<LanguageEntry> All => _all;

    /// <summary>
    /// Read through the config store, so a registry the reader has edited is the
    /// one that loads. Falls back to the shipped copy when nothing is
    /// configured, or when what is configured does not parse.
    ///
    /// An empty registry rather than a throw, matching the original: the caller
    /// decides whether having no languages is fatal.
    /// </summary>
    private static IReadOnlyList<LanguageEntry> LoadEmbedded() =>
        BomSafeJson.Deserialize<LanguageDocument>(Config.ConfigStore.BytesFor("languages"))?.Languages ?? [];

    /// <summary>
    /// Accepts a code, an alias or an English name, in any case, and returns the
    /// entry. "cs", "CS", "Czech" and "czech" are the same language; "ua"
    /// resolves to the entry whose canonical code is "uk".
    ///
    /// Returns null for anything unknown -- the caller decides whether that is
    /// fatal. The order of the passes is the original's and is load-bearing: an
    /// exact code beats an exact name, which beats an alias, which beats a
    /// native name, and only then is a prefix tried.
    /// </summary>
    public LanguageEntry? Resolve(string? name)
    {
        var q = name?.Trim();

        if (string.IsNullOrEmpty(q))
        {
            return null;
        }

        var byCode = _all.FirstOrDefault(l => Same(l.Code, q));
        if (byCode is not null)
        {
            return byCode;
        }

        var byName = _all.FirstOrDefault(l => Same(l.Name, q));
        if (byName is not null)
        {
            return byName;
        }

        var byAlias = _all.FirstOrDefault(l => l.Alias?.Any(a => Same(a, q)) == true);
        if (byAlias is not null)
        {
            return byAlias;
        }

        var byNative = _all.FirstOrDefault(l => Same(l.Native, q));
        if (byNative is not null)
        {
            return byNative;
        }

        // Last resort: a unique prefix of the English name, so "Norweg" works.
        // Unique is the point -- an ambiguous prefix resolves to nothing rather
        // than to whichever row happened to come first.
        var prefixed = _all
            .Where(l => l.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return prefixed.Count == 1 ? prefixed[0] : null;
    }

    private static readonly ModelLanguages Models = new();

    /// <summary>
    /// Which model family a model id maps to, or empty for a model the
    /// catalogue has no entry for. The catalogue is ordered, so the more
    /// specific name wins: "translategemma" is matched before "eurollm".
    /// </summary>
    public static string ModelFlag(string? modelId) => Models.For(modelId)?.Family ?? string.Empty;

    /// <summary>True when the registry actually knows this model's training set.</summary>
    public static bool IsVerified(string? modelId) => ModelFlag(modelId).Length > 0;

    /// <summary>
    /// The languages worth offering for the model actually loaded.
    ///
    /// A model the catalogue has no entry for returns everything, because
    /// absence of evidence is not evidence of absence -- the operator is warned
    /// rather than blocked. So does a family whose language set could not be
    /// sourced: unverified is not the same as unsupported.
    /// </summary>
    public IReadOnlyList<LanguageEntry> ForModel(string? modelId)
    {
        var family = Models.For(modelId);

        if (family is null || !family.HasSource)
        {
            return _all;
        }

        var codes = family.Codes;

        return [.. _all.Where(l => codes.Contains(l.Code))];
    }

    /// <summary>
    /// The warning to print before translating, or empty when there is nothing
    /// to say. Separate from <see cref="Resolve"/> so a caller can resolve
    /// quietly.
    ///
    /// The wording is the original's, verbatim, including the figure 35. The
    /// registry marks 36 rows as EuroLLM languages, so the sentence and the data
    /// disagree; the sentence is what operators have been reading and is
    /// user-facing, so the port carries it unchanged rather than quietly
    /// correcting a number that belongs to the reference engine.
    /// </summary>
    public static string Warning(LanguageEntry? language, string? modelId = null)
    {
        if (language is null)
        {
            return string.Empty;
        }

        if (Models.All.FirstOrDefault(f => f.Family == "eurollm")?.Codes.Contains(language.Code) == true)
        {
            return string.Empty;
        }

        // Only EuroLLM's training set is known here. Another model may be fine,
        // and saying so is more honest than implying this engine knows every
        // model.
        if (!string.IsNullOrEmpty(modelId)
            && !modelId.Contains("eurollm", StringComparison.OrdinalIgnoreCase))
        {
            return $"{language.Name} is outside EuroLLM's 35 trained languages. '{modelId}' may handle it; nothing here can check the meaning of what comes back.";
        }

        return $"{language.Name} is NOT one of EuroLLM's 35 trained languages. It will answer anyway, and every gate here checks structure rather than meaning - so a wrong translation will pass. Load a model trained on {language.Name}, or treat the output as a draft.";
    }

    private static bool Same(string? a, string b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
