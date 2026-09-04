using System.Runtime.CompilerServices;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public interface ITagger
{
    string Language { get; }

    bool Available { get; }

    string UnavailableReason { get; }

    IReadOnlyList<string> Tags(string word);
}

public sealed class UnavailableTagger(string language, string reason) : ITagger
{
    public string Language { get; } = language;

    public bool Available => false;

    public string UnavailableReason { get; } = reason;

    public IReadOnlyList<string> Tags(string word) => [];
}

public sealed class TableTagger(string language) : ITagger
{
    private readonly Dictionary<string, List<string>> _tags = new(StringComparer.OrdinalIgnoreCase);

    public string Language { get; } = language;

    public bool Available => true;

    public string UnavailableReason => string.Empty;

    public TableTagger Add(string word, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (!_tags.TryGetValue(word, out var list))
        {
            list = [];
            _tags[word] = list;
        }

        list.AddRange(tags);
        return this;
    }

    public IReadOnlyList<string> Tags(string word) => _tags.TryGetValue(word, out var list) ? list : [];
}

public sealed record NaturalnessEvidence(string CheckId, string UnitIdentity, CheckRange TargetRange, CheckRange? SourceRange, string Evidence);

public sealed class NaturalnessEvidenceStore
{
    private static readonly ConditionalWeakTable<CheckContext, NaturalnessEvidenceStore> Attached = new();

    private readonly List<NaturalnessEvidence> _items = [];

    private readonly Dictionary<string, int> _expectedSentences = new(StringComparer.Ordinal);

    public IReadOnlyList<NaturalnessEvidence> Items => _items;

    public IReadOnlyDictionary<string, int> ExpectedSentences => _expectedSentences;

    public static NaturalnessEvidenceStore For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Attached.GetValue(context, _ => new NaturalnessEvidenceStore());
    }

    public void Add(NaturalnessEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!_items.Any(i => i.CheckId == evidence.CheckId && i.TargetRange == evidence.TargetRange && i.Evidence == evidence.Evidence))
        {
            _items.Add(evidence);
        }
    }

    public void ExpectSentences(string unitIdentity, int count) => _expectedSentences[unitIdentity] = count;

    public IReadOnlyList<NaturalnessEvidence> ForRange(CheckRange range) => [.. _items.Where(i => i.TargetRange.Overlaps(range))];
}

public sealed class NaturalnessServices
{
    public const string NotAttachedReason = "no naturalness services were attached to this run";

    public NaturalnessServices(IReadOnlyDictionary<string, PackLoadResult> packs, ITagger tagger, NaturalnessProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(tagger);

        Packs = packs;
        Tagger = tagger;
        Profile = profile ?? NaturalnessProfile.Default;
    }

    public IReadOnlyDictionary<string, PackLoadResult> Packs { get; }

    public ITagger Tagger { get; }

    public NaturalnessProfile Profile { get; }

    public static NaturalnessServices Unavailable(string language, string reason) =>
        new(NaturalnessPackLoader.ShippedPacks, new UnavailableTagger(language, reason));

    public static NaturalnessServices Shipped(ITagger tagger, NaturalnessProfile? profile = null) =>
        new(NaturalnessPackLoader.ShippedPacks, tagger, profile);

    public PackLoadResult? PackResult(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var primary = language.Split('-', '_')[0];

        return Packs.TryGetValue(language, out var exact) ? exact : Packs.TryGetValue(primary, out var byPrimary) ? byPrimary : null;
    }

    public NaturalnessPack? PackFor(string language) => PackResult(language)?.Pack;

    public bool AnalyzerAvailableFor(string language) =>
        Tagger.Available && string.Equals(Tagger.Language.Split('-', '_')[0], language.Split('-', '_')[0], StringComparison.OrdinalIgnoreCase);

    public string? SkipReason(CheckRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.TargetLanguage.Length == 0)
        {
            return "run declares no target language";
        }

        var result = PackResult(settings.TargetLanguage);

        if (result is null)
        {
            return "no naturalness pack for " + settings.TargetLanguage + "; the coverage matrix records the requirement";
        }

        return result.Loaded ? null : "pack for " + settings.TargetLanguage + " disabled: " + result.Reason;
    }

    public string? LoadNotice(string language)
    {
        var pack = PackFor(language);

        if (pack is null)
        {
            return null;
        }

        return AnalyzerAvailableFor(language)
            ? null
            : "no morphological analyzer for " + language + " (" + Tagger.UnavailableReason + "); decidable rules that read tags run as model judgment";
    }
}

public static class NaturalnessPorts
{
    private static readonly ConditionalWeakTable<CheckContext, NaturalnessServices> Attached = new();

    public static void Attach(CheckContext context, NaturalnessServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        Attached.AddOrUpdate(context, services);
    }

    public static NaturalnessServices For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Attached.TryGetValue(context, out var services)
            ? services
            : NaturalnessServices.Unavailable(context.Settings.TargetLanguage, NaturalnessServices.NotAttachedReason);
    }
}
