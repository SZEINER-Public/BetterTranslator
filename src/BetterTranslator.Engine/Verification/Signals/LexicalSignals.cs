using System.Collections.Concurrent;
using System.Diagnostics;

namespace BetterTranslator.Engine.Verification.Signals;

public interface ISpellChecker
{
    bool IsCorrect(string word);
}

public interface IMorphologicalAnalyzer
{
    bool TryAnalyze(string word, out int lemmaCount);
}

public sealed class NullMorphologicalAnalyzer : IMorphologicalAnalyzer
{
    public static readonly NullMorphologicalAnalyzer Instance = new();

    public bool TryAnalyze(string word, out int lemmaCount)
    {
        lemmaCount = 0;
        return false;
    }
}

public sealed class HunspellSpellChecker : ISpellChecker
{
    private readonly WeCantSpell.Hunspell.WordList _list;

    public HunspellSpellChecker(string dicPath, string affPath)
    {
        _list = WeCantSpell.Hunspell.WordList.CreateFromFiles(dicPath, affPath);
    }

    public bool IsCorrect(string word) => _list.Check(word);
}

public sealed class MajkaCliAnalyzer : IMorphologicalAnalyzer, IDisposable
{
    private readonly string _exePath;
    private readonly string _dictPath;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, int> _cache = new(StringComparer.Ordinal);

    public MajkaCliAnalyzer(string exePath, string dictPath, TimeSpan? timeout = null)
    {
        _exePath = exePath;
        _dictPath = dictPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public bool TryAnalyze(string word, out int lemmaCount)
    {
        if (!File.Exists(_exePath) || !File.Exists(_dictPath))
        {
            lemmaCount = 0;
            return false;
        }

        lemmaCount = _cache.GetOrAdd(word, RunMajka);
        return lemmaCount >= 0;
    }

    private int RunMajka(string word)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(_dictPath);
            psi.ArgumentList.Add("-l");

            using var p = Process.Start(psi);

            if (p is null)
            {
                return -1;
            }

            p.StandardInput.WriteLine(word);
            p.StandardInput.Close();

            if (!p.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                try
                {
                    p.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                return -1;
            }

            var count = 0;
            string? line;

            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                var idx = line.IndexOf(':', StringComparison.Ordinal);

                if (idx >= 0 && idx < line.Length - 1)
                {
                    count += CountSegments(line[(idx + 1)..]);
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return -1;
        }
    }

    private static int CountSegments(string s)
    {
        var c = 1;

        foreach (var ch in s)
        {
            if (ch == ':')
            {
                c++;
            }
        }

        return c;
    }

    public void Dispose()
    {
    }
}

public sealed class SourceSpanExemption
{
    private readonly HashSet<string> _candidates = new(StringComparer.Ordinal);

    public SourceSpanExemption(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        foreach (var (token, _) in WordTokenizer.Tokenize(sourceText))
        {
            var hasDigitOrUnderscore = token.IndexOfAny("0123456789_".ToCharArray()) >= 0;

            if (hasDigitOrUnderscore || FusedTokenCheck.HasCamelBoundary(token))
            {
                _candidates.Add(token);
            }
        }

        RescanWithSentenceBoundaries(sourceText);
    }

    private void RescanWithSentenceBoundaries(string sourceText)
    {
        var boundary = true;
        var i = 0;

        while (i < sourceText.Length)
        {
            var c = sourceText[i];

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;

                while (i < sourceText.Length && (char.IsLetterOrDigit(sourceText[i]) || sourceText[i] == '_'))
                {
                    i++;
                }

                var token = sourceText[start..i];

                if (char.IsUpper(token[0]) && !boundary)
                {
                    _candidates.Add(token);
                }

                boundary = false;
            }
            else
            {
                if (c is '.' or '!' or '?' or '\n')
                {
                    boundary = true;
                }

                i++;
            }
        }
    }

    public bool IsExempt(string targetWord) => _candidates.Contains(targetWord);
}

public interface ITermConsistencySignal
{
    bool TryEvaluate(string sourceText, string targetWord, out int penalty, out string detail);
}

public static class WordTokenizer
{
    public static IEnumerable<(string Word, int Start)> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var i = 0;

        while (i < text.Length)
        {
            if (char.IsLetter(text[i]))
            {
                var start = i;

                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                yield return (text[start..i], start);
            }
            else
            {
                i++;
            }
        }
    }
}
