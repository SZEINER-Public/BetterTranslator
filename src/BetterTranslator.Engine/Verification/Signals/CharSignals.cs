using System.Globalization;

namespace BetterTranslator.Engine.Verification.Signals;

public static class CharLegalityCheck
{
    private const string Vowels = "aeiouyáéíóúůýě";

    public static string? Check(string word)
    {
        var w = word.ToLowerInvariant();

        for (var i = 1; i < w.Length; i++)
        {
            if (w[i] == w[i - 1] && Vowels.IndexOf(w[i], StringComparison.Ordinal) >= 0)
            {
                return $"doubled vowel '{w[i]}{w[i]}'";
            }

            if (i >= 2 && w[i] == w[i - 1] && w[i] == w[i - 2])
            {
                return $"tripled character '{w[i]}{w[i]}{w[i]}'";
            }
        }

        bool sawLatin = false, sawOther = false;

        foreach (var ch in word)
        {
            if (!char.IsLetter(ch))
            {
                continue;
            }

            if (IsLatin(ch))
            {
                sawLatin = true;
            }
            else
            {
                sawOther = true;
            }
        }

        return sawLatin && sawOther ? "mixed script" : null;
    }

    private static bool IsLatin(char ch) =>
        (ch >= 'A' && ch <= 'Z')
        || (ch >= 'a' && ch <= 'z')
        || (ch >= 'À' && ch <= 'ɏ');
}

public static class FusedTokenCheck
{
    public static bool HasCamelBoundary(string word)
    {
        for (var i = 1; i < word.Length; i++)
        {
            if (char.IsLower(word[i - 1]) && char.IsUpper(word[i]))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class FrequencyLexicon
{
    private readonly Dictionary<string, long> _freq = new(StringComparer.Ordinal);

    public static FrequencyLexicon LoadFile(string path)
    {
        var lex = new FrequencyLexicon();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var sep = line.IndexOfAny(['\t', ' ']);
            string word;
            long f = 1;

            if (sep < 0)
            {
                word = line;
            }
            else
            {
                word = line[..sep];
                _ = long.TryParse(line[(sep + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out f);

                if (f <= 0)
                {
                    f = 1;
                }
            }

            var key = word.ToLowerInvariant();
            lex._freq.TryGetValue(key, out var existing);
            lex._freq[key] = existing + f;
        }

        return lex;
    }

    public static FrequencyLexicon FromPairs(IEnumerable<(string Word, long Freq)> pairs)
    {
        var lex = new FrequencyLexicon();

        foreach (var (w, f) in pairs)
        {
            lex._freq[w.ToLowerInvariant()] = f;
        }

        return lex;
    }

    public long FrequencyOf(string word) => _freq.TryGetValue(word.ToLowerInvariant(), out var f) ? f : 0;

    public bool BelowFloor(string word, long minFrequency) => FrequencyOf(word) < minFrequency;

    public IEnumerable<KeyValuePair<string, long>> Entries => _freq;
}

public sealed class CharNgramScorer
{
    private const char Boundary = '';

    private readonly Dictionary<(char, char), double> _bigram = [];
    private readonly Dictionary<char, double> _unigram = [];
    private readonly double _vocab;

    public CharNgramScorer(FrequencyLexicon lexicon)
    {
        ArgumentNullException.ThrowIfNull(lexicon);

        var alphabet = new HashSet<char> { Boundary };

        foreach (var entry in lexicon.Entries)
        {
            var w = Boundary + entry.Key + Boundary;
            var weight = Math.Log(1 + entry.Value);

            for (var i = 1; i < w.Length; i++)
            {
                alphabet.Add(w[i]);

                var key = (w[i - 1], w[i]);
                _bigram.TryGetValue(key, out var b);
                _bigram[key] = b + weight;

                _unigram.TryGetValue(w[i - 1], out var u);
                _unigram[w[i - 1]] = u + weight;
            }
        }

        _vocab = alphabet.Count;
    }

    public double AverageLogProb(string word)
    {
        var w = Boundary + word.ToLowerInvariant() + Boundary;
        double sum = 0;
        var n = 0;

        for (var i = 1; i < w.Length; i++, n++)
        {
            _bigram.TryGetValue((w[i - 1], w[i]), out var b);
            _unigram.TryGetValue(w[i - 1], out var u);
            sum += Math.Log((b + 1.0) / (u + _vocab));
        }

        return n == 0 ? 0 : sum / n;
    }
}
