namespace BetterTranslator.Engine.Verification;

/// <summary>A Hunspell pair on disk: the word list and the affix rules beside it.</summary>
public sealed record DictionaryPair(string DicPath, string AffPath);

/// <summary>
/// Finds the Hunspell pair for a language without anyone configuring a path.
///
/// The verifier needs a dic and an aff, and until now both had to be typed into
/// settings that no screen exposes, so the word-level pass was inert on every
/// machine. Dropping two files into a folder is the whole setup this replaces.
///
/// A pair is only a pair when both halves are there. A dic without its aff loads
/// as an exception rather than as a dictionary, so a half-installed language is
/// treated as absent and the verifier stays off instead of failing per word.
///
/// Naming follows the convention every published Czech dictionary already uses,
/// <c>cs_CZ.dic</c> beside <c>cs_CZ.aff</c>, and the region is not guessed from
/// the language: a folder holding <c>cs_CZ</c> answers a request for <c>cs</c>
/// through the trailing search, while a request for <c>en</c> never invents
/// <c>en_EN</c>.
/// </summary>
public static class DictionaryStore
{
    /// <summary>
    /// Points the search at a folder outside the data root, for a machine that
    /// keeps one shared set of dictionaries. Mirrors the model store override.
    /// </summary>
    public const string EnvironmentOverride = "BETTERTRANSLATOR_DICTIONARY_STORE";

    /// <summary>The folder name looked for beside the executable.</summary>
    public const string FolderName = "dictionaries";

    /// <summary>
    /// Where to look, best first: an explicit override, then the reader's own
    /// data folder, then whatever shipped beside the executable. The reader's
    /// copy wins, which is the same precedence the config store applies to an
    /// edited file over the embedded default.
    /// </summary>
    public static IReadOnlyList<string> Folders(string? dataFolder)
    {
        var folders = new List<string>();

        Add(Environment.GetEnvironmentVariable(EnvironmentOverride));
        Add(dataFolder);
        Add(Path.Combine(AppContext.BaseDirectory, FolderName));

        return folders;

        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var full = SafeFullPath(folder);

            if (full is not null && !folders.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(full);
            }
        }
    }

    /// <summary>
    /// The pair for a language, or null when this machine has none. A blank code
    /// answers null rather than picking whichever dictionary is present: marking
    /// Czech output against a German word list would flag every word in it.
    /// </summary>
    public static DictionaryPair? Find(string? languageCode, string? dataFolder)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        foreach (var folder in Folders(dataFolder))
        {
            foreach (var stem in StemsFor(languageCode))
            {
                var pair = PairAt(folder, stem);

                if (pair is not null)
                {
                    return pair;
                }
            }

            var regional = RegionalPairIn(folder, BaseOf(languageCode));

            if (regional is not null)
            {
                return regional;
            }
        }

        return null;
    }

    /// <summary>
    /// The file stems tried for a code, in order and without duplicates. The
    /// underscore form first because it is what the published dictionaries use.
    /// </summary>
    internal static IReadOnlyList<string> StemsFor(string languageCode)
    {
        var code = languageCode.Trim();

        List<string> stems = [];

        foreach (var candidate in new[] { code.Replace('-', '_'), code, BaseOf(code) })
        {
            if (candidate.Length > 0 && !stems.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                stems.Add(candidate);
            }
        }

        return stems;
    }

    private static string BaseOf(string languageCode)
    {
        var code = languageCode.Trim();
        var cut = code.IndexOfAny(['-', '_']);

        return cut < 0 ? code : code[..cut];
    }

    /// <summary>
    /// The region-qualified pair for a bare language, so a folder holding only
    /// <c>cs_CZ</c> answers a request for <c>cs</c>. Ordinal-first, so a folder
    /// with two regions of one language resolves the same way on every machine.
    /// </summary>
    private static DictionaryPair? RegionalPairIn(string folder, string baseCode)
    {
        if (baseCode.Length == 0)
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            var stems = Directory
                .EnumerateFiles(folder, baseCode + "?*.dic")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(stem => stem is not null && Separated(stem, baseCode))
                .Select(stem => stem!)
                .OrderBy(stem => stem, StringComparer.Ordinal);

            return stems.Select(stem => PairAt(folder, stem)).FirstOrDefault(pair => pair is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for <c>cs_CZ</c> against <c>cs</c> and false for <c>csb</c>: the
    /// glob matches any following character, and only a separator makes the rest
    /// of the name a region rather than a different language.
    /// </summary>
    private static bool Separated(string stem, string baseCode) =>
        stem.Length > baseCode.Length && stem[baseCode.Length] is '_' or '-';

    private static DictionaryPair? PairAt(string folder, string stem)
    {
        try
        {
            var dic = Path.Combine(folder, stem + ".dic");
            var aff = Path.Combine(folder, stem + ".aff");

            return File.Exists(dic) && File.Exists(aff) ? new DictionaryPair(dic, aff) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? SafeFullPath(string folder)
    {
        try
        {
            return Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
