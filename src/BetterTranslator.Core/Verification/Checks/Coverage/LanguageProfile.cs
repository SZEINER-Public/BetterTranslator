namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class LanguageProfile
{
    private const int Order = 3;

    private const char Boundary = ' ';

    public const double AlphabetCoverageFloor = 0.6;

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _contexts = new(StringComparer.Ordinal);

    private readonly HashSet<char> _alphabet = [];

    private LanguageProfile(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public int Total { get; private set; }

    public static LanguageProfile Train(string code, params string[] samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(samples);

        var profile = new LanguageProfile(code);

        foreach (var sample in samples)
        {
            profile.Add(sample);
        }

        return profile;
    }

    public LanguageProfile Add(string sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        foreach (var word in Words(sample))
        {
            var padded = Boundary + word + Boundary;

            for (var i = 0; i + Order <= padded.Length; i++)
            {
                var gram = padded.Substring(i, Order);
                var context = gram[..(Order - 1)];
                _counts[gram] = _counts.GetValueOrDefault(gram) + 1;
                _contexts[context] = _contexts.GetValueOrDefault(context) + 1;
                Total++;
            }

            foreach (var c in word)
            {
                _alphabet.Add(c);
            }
        }

        return this;
    }

    public double AverageLogProbability(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sum = 0.0;
        var grams = 0;
        var vocabulary = Math.Max(_alphabet.Count + 1, 2);
        var letters = 0;
        var covered = 0;

        foreach (var word in Words(text))
        {
            var padded = Boundary + word + Boundary;

            foreach (var c in word)
            {
                letters++;
                covered += _alphabet.Contains(c) ? 1 : 0;
            }

            for (var i = 0; i + Order <= padded.Length; i++)
            {
                var gram = padded.Substring(i, Order);
                var context = gram[..(Order - 1)];
                var count = _counts.GetValueOrDefault(gram);
                var seen = _contexts.GetValueOrDefault(context);
                sum += Math.Log((count + 0.5) / (seen + 0.5 * vocabulary));
                grams++;
            }
        }

        return grams == 0 || covered < letters * AlphabetCoverageFloor ? double.NegativeInfinity : sum / grams + Math.Log((double)covered / letters);
    }

    public static IEnumerable<string> Words(string text)
    {
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var letter = i < text.Length && char.IsLetter(text[i]);

            if (letter && start < 0)
            {
                start = i;
            }
            else if (!letter && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }
}

public sealed record LanguageIdentification(string? Code, int Confidence)
{
    public static LanguageIdentification Undetermined { get; } = new(null, 0);

    public bool IsUndetermined => Code is null;
}

public sealed class CharNgramLanguageIdentifier
{
    public const int CharacterFloor = 20;

    private const double DecisionMargin = 0.08;

    public const int SoleScriptConfidence = 85;

    private readonly IReadOnlyList<LanguageProfile> _profiles;

    public CharNgramLanguageIdentifier(IEnumerable<LanguageProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        _profiles = [.. profiles.OrderBy(p => p.Code, StringComparer.Ordinal)];
    }

    public IReadOnlyList<LanguageProfile> Profiles => _profiles;

    public LanguageProfile? Profile(string code) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? _profiles.FirstOrDefault(p => string.Equals(LanguageSeeds.Primary(p.Code), LanguageSeeds.Primary(code), StringComparison.OrdinalIgnoreCase));

    public bool Knows(string code) => Profile(code) is not null;

    public static int MeasuredLength(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var length = 0;
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = length > 0;
                continue;
            }

            if (pendingSpace)
            {
                length++;
                pendingSpace = false;
            }

            length++;
        }

        return length;
    }

    public LanguageIdentification Identify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (MeasuredLength(text) <= CharacterFloor || _profiles.Count < 2)
        {
            return LanguageIdentification.Undetermined;
        }

        var scored = _profiles
            .Select(p => (p.Code, Score: p.AverageLogProbability(text)))
            .Where(s => !double.IsNegativeInfinity(s.Score))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0)
        {
            return LanguageIdentification.Undetermined;
        }

        if (scored.Count == 1)
        {
            return new LanguageIdentification(scored[0].Code, SoleScriptConfidence);
        }

        var margin = scored[0].Score - scored[1].Score;

        if (margin < DecisionMargin)
        {
            return LanguageIdentification.Undetermined;
        }

        var confidence = (int)Math.Round(Math.Clamp(50 + 100 * margin, 0, 100), MidpointRounding.AwayFromZero);

        return new LanguageIdentification(scored[0].Code, confidence);
    }
}
