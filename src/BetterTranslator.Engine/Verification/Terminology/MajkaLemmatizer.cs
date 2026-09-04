using System.Collections.Concurrent;
using System.Diagnostics;
using BetterTranslator.Core.Verification.Checks.Terminology;

namespace BetterTranslator.Engine.Verification.Terminology;

public sealed class MajkaLemmatizer : ILemmatizer
{
    private readonly string _exePath;
    private readonly string _dictPath;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public MajkaLemmatizer(string language, string exePath, string dictPath, TimeSpan? timeout = null)
    {
        Language = language;
        _exePath = exePath;
        _dictPath = dictPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public string Language { get; }

    public bool Available => File.Exists(_exePath) && File.Exists(_dictPath);

    public string UnavailableReason =>
        Available ? string.Empty : "majka executable or dictionary not found for " + Language;

    public static ILemmatizer Resolve(string language, string? exePath, string? dictPath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(dictPath))
        {
            return new UnavailableLemmatizer(language, "majka is not configured for " + language);
        }

        return new MajkaLemmatizer(language, exePath, dictPath);
    }

    public string? Lemma(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (!Available || word.Length == 0)
        {
            return null;
        }

        return _cache.GetOrAdd(word, RunMajka);
    }

    private string? RunMajka(string word)
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
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(_dictPath);
            psi.ArgumentList.Add("-l");

            using var process = Process.Start(psi);

            if (process is null)
            {
                return null;
            }

            process.StandardInput.WriteLine(word);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            string? line;

            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                var lemma = FirstLemma(line);

                if (lemma is not null)
                {
                    return lemma;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? FirstLemma(string outputLine)
    {
        ArgumentNullException.ThrowIfNull(outputLine);

        var parts = outputLine.Split(':');

        if (parts.Length < 2)
        {
            return null;
        }

        var lemmas = new List<string>();

        for (var i = 1; i < parts.Length; i += 2)
        {
            if (parts[i].Length > 0)
            {
                lemmas.Add(parts[i]);
            }
        }

        return lemmas.Count == 0 ? null : lemmas.Order(StringComparer.Ordinal).First();
    }
}
