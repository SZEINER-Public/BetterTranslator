using System.Collections.Concurrent;
using System.Diagnostics;
using BetterTranslator.Core.Verification.Checks.Naturalness;

namespace BetterTranslator.Engine.Verification.Naturalness;

public sealed class MajkaTagger : ITagger
{
    private readonly string _exePath;
    private readonly string _dictPath;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.Ordinal);

    public MajkaTagger(string language, string exePath, string dictPath, TimeSpan? timeout = null)
    {
        Language = language;
        _exePath = exePath;
        _dictPath = dictPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public string Language { get; }

    public bool Available => File.Exists(_exePath) && File.Exists(_dictPath);

    public string UnavailableReason => Available ? string.Empty : "majka executable or dictionary not found for " + Language;

    public static ITagger Resolve(string language, string? exePath, string? dictPath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(dictPath))
        {
            return new UnavailableTagger(language, "majka is not configured for " + language);
        }

        return new MajkaTagger(language, exePath, dictPath);
    }

    public IReadOnlyList<string> Tags(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (!Available || word.Length == 0)
        {
            return [];
        }

        return _cache.GetOrAdd(word, RunMajka);
    }

    private IReadOnlyList<string> RunMajka(string word)
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
                return [];
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

                return [];
            }

            var tags = new List<string>();
            string? line;

            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                tags.AddRange(TagsOf(line));
            }

            return tags;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> TagsOf(string outputLine)
    {
        ArgumentNullException.ThrowIfNull(outputLine);

        var parts = outputLine.Split(':');
        var tags = new List<string>();

        for (var i = 2; i < parts.Length; i += 2)
        {
            if (parts[i].Length > 0)
            {
                tags.Add(parts[i]);
            }
        }

        return tags;
    }
}
