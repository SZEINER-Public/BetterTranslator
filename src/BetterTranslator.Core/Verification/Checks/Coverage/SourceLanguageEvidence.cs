namespace BetterTranslator.Core.Verification.Checks.Coverage;

public interface ISourceLexicon
{
    string Code { get; }

    bool Contains(string word);
}

public interface ISourceMorphology
{
    string Code { get; }

    bool Analyzes(string word);
}

public sealed record SourceSignals(bool Lexicon, bool Morphology, bool Ngram)
{
    public int Count => (Lexicon ? 1 : 0) + (Morphology ? 1 : 0) + (Ngram ? 1 : 0);

    public string Describe()
    {
        var names = new List<string>(3);

        if (Lexicon)
        {
            names.Add("lexicon");
        }

        if (Morphology)
        {
            names.Add("morphology");
        }

        if (Ngram)
        {
            names.Add("ngram");
        }

        return names.Count == 0 ? "none" : string.Join("+", names);
    }
}

public sealed class WordListLexicon(string code, IEnumerable<string> words) : ISourceLexicon
{
    private readonly HashSet<string> _words = [.. words.Select(w => w.ToLowerInvariant())];

    public string Code { get; } = code;

    public bool Contains(string word) => _words.Contains(word.ToLowerInvariant());
}

public sealed class SuffixMorphology(string code, ISourceLexicon stems, IReadOnlyList<string> suffixes) : ISourceMorphology
{
    public string Code { get; } = code;

    public bool Analyzes(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var lower = word.ToLowerInvariant();

        foreach (var suffix in suffixes.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal))
        {
            if (lower.Length <= suffix.Length + 2 || !lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var stem = lower[..^suffix.Length];

            if (stems.Contains(stem) || stems.Contains(stem + "e"))
            {
                return true;
            }

            if (stem.Length > 2 && stem[^1] == stem[^2] && stems.Contains(stem[..^1]))
            {
                return true;
            }

            if (stem.EndsWith('i') && stems.Contains(stem[..^1] + "y"))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class SourceLanguageEvidence
{
    public const double NgramMargin = 0.35;

    public const int NgramMinimumLength = 4;

    private readonly IReadOnlyList<ISourceLexicon> _lexicons;

    private readonly IReadOnlyList<ISourceMorphology> _morphologies;

    public SourceLanguageEvidence(
        IEnumerable<ISourceLexicon> lexicons,
        IEnumerable<ISourceMorphology> morphologies,
        CharNgramLanguageIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(lexicons);
        ArgumentNullException.ThrowIfNull(morphologies);
        ArgumentNullException.ThrowIfNull(identifier);

        _lexicons = [.. lexicons];
        _morphologies = [.. morphologies];
        Identifier = identifier;
    }

    public CharNgramLanguageIdentifier Identifier { get; }

    public SourceSignals Evaluate(string word, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (word.Length == 0 || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new SourceSignals(false, false, false);
        }

        var lexicon = _lexicons.Any(l => Matches(l.Code, sourceLanguage) && l.Contains(word));
        var morphology = _morphologies.Any(m => Matches(m.Code, sourceLanguage) && m.Analyzes(word));
        var ngram = NgramFavorsSource(word, sourceLanguage, targetLanguage);

        return new SourceSignals(lexicon, morphology, ngram);
    }

    private bool NgramFavorsSource(string word, string sourceLanguage, string targetLanguage)
    {
        if (word.Length < NgramMinimumLength)
        {
            return false;
        }

        var source = Identifier.Profile(sourceLanguage);

        if (source is null)
        {
            return false;
        }

        var sourceScore = source.AverageLogProbability(word);
        var rivals = Identifier.Profiles
            .Where(p => !Matches(p.Code, sourceLanguage))
            .Where(p => string.IsNullOrWhiteSpace(targetLanguage) || Matches(p.Code, targetLanguage))
            .ToList();

        if (rivals.Count == 0)
        {
            return false;
        }

        var best = rivals.Max(p => p.AverageLogProbability(word));

        return sourceScore - best >= NgramMargin;
    }

    private static bool Matches(string code, string language)
    {
        var primary = language.Split('-', '_')[0];
        return string.Equals(code, language, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, primary, StringComparison.OrdinalIgnoreCase);
    }
}

public static class CoverageServices
{
    private static readonly Lazy<SourceLanguageEvidence> Seeded = new(BuildSeeded);

    private static SourceLanguageEvidence? _configured;

    public static SourceLanguageEvidence Default => _configured ?? Seeded.Value;

    public static void Configure(SourceLanguageEvidence? evidence) => _configured = evidence;

    public static SourceLanguageEvidence BuildSeeded()
    {
        var lexicon = new WordListLexicon("en", EnglishSeedLexicon.Words);

        return new SourceLanguageEvidence(
            [lexicon],
            [new SuffixMorphology("en", lexicon, EnglishSeedLexicon.Suffixes)],
            new CharNgramLanguageIdentifier(LanguageSeeds.Profiles()));
    }
}
