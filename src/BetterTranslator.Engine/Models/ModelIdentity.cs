using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Models;

/// <summary>
/// Deciding whether two names refer to the same model. Ported from
/// `Get-MatchToken` in `resolve-model.ps1` and `Test-ModelPresent` in
/// `store-resolve.ps1`.
///
/// This looks like string comparison and is not. A model is named one way by its
/// publisher, another by the hub URL, another by the tool that downloaded it, and
/// a fourth by the folder it landed in -- and getting it wrong is expensive in
/// both directions: a false negative re-downloads fifteen gigabytes, a false
/// positive loads a different quantisation and translates badly for reasons
/// nothing in the log explains.
/// </summary>
public static class ModelIdentity
{
    private static readonly Regex PinnedQuant = new(@"@[^/@]+$", RegexOptions.Compiled);
    private static readonly Regex Scheme = new("^[a-z]+://", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QueryOrFragment = new(@"[?#].*$", RegexOptions.Compiled);
    private static readonly Regex PackagingSuffix = new(@"[-_]gguf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NotAlphanumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Reduces a spec -- a name, a hub URL, a pinned build -- to the token the
    /// rest of this class compares on.
    ///
    /// A pinned quantisation is part of the REQUEST, not part of the name. `lms
    /// get` documents "name@quant" for choosing a build, so a spec may arrive as
    /// ".../translategemma-4b-it-GGUF@Q4_K_M". Left attached, the last segment
    /// becomes "translategemma-4b-it-gguf@q4_k_m", the "-gguf" strip below no
    /// longer anchors, and the token matches nothing -- a model that downloaded
    /// perfectly is then reported as not present.
    ///
    /// Stripped here rather than at the call site because every caller that pins
    /// a quant would otherwise have to remember to. Safe on a URL: "@" is only
    /// legal earlier, in userinfo, never in a trailing path segment.
    /// </summary>
    public static string Token(string? spec)
    {
        var token = PinnedQuant.Replace((spec ?? string.Empty).Trim(), string.Empty);

        if (Scheme.IsMatch(token))
        {
            // Hub URL: the repository name is the last meaningful segment.
            token = QueryOrFragment.Replace(token, string.Empty).TrimEnd('/');
            token = token[(token.LastIndexOf('/') + 1)..];

            // The tools that download it drop the packaging suffix when they
            // name the model, so a token that keeps it matches nothing on disk.
            token = PackagingSuffix.Replace(token, string.Empty);
        }

        return token.ToLowerInvariant();
    }

    /// <summary>The alphanumeric runs of a name, lowercased.</summary>
    public static IReadOnlyList<string> Tokens(string? value) =>
    [
        .. NotAlphanumeric
            .Split(value ?? string.Empty)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant()),
    ];

    /// <summary>Every separator removed, lowercased. "gemma-4-26b" becomes "gemma426b".</summary>
    public static string Flatten(string? value) =>
        NotAlphanumeric.Replace(value ?? string.Empty, string.Empty).ToLowerInvariant();

    /// <summary>
    /// Does <paramref name="candidate"/> name the model <paramref name="wanted"/>
    /// asks for?
    ///
    /// Three tests, because each covers a failure the others do not.
    ///
    /// Literal, for the ordinary case. Flattened, because "gemma-3" and "gemma3"
    /// are the same model written by two tools. And token-subset, because a
    /// flattened test fails on exactly the case that matters most: the identifier
    /// google/gemma-4-26b-a4b-qat flattens to "gemma426ba4bqat", while the
    /// directory it lives in flattens to "gemma426ba4bitqatgguf" -- the inserted
    /// "it" breaks the substring, the model is reported absent, and fifteen
    /// gigabytes are downloaded again. Requiring every token of the wanted name
    /// to appear as a token of the candidate survives inserted and appended
    /// segments.
    /// </summary>
    public static bool Matches(string? candidate, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(wanted))
        {
            return false;
        }

        if (candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var flatWanted = Flatten(wanted);

        if (flatWanted.Length > 0 && Flatten(candidate).Contains(flatWanted, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = Tokens(wanted);

        if (tokens.Count == 0)
        {
            return false;
        }

        var haystack = new HashSet<string>(Tokens(candidate), StringComparer.Ordinal);

        return tokens.All(haystack.Contains);
    }

    /// <summary>
    /// Is a model matching this spec already somewhere under this folder?
    ///
    /// The haystack for each file is its own name plus the two folders above it,
    /// because a real store nests publisher/repo/file and the identifying words
    /// are spread across all three -- "google", "translategemma-4b-it" and the
    /// quantised filename each carry part of the name.
    /// </summary>
    public static string? FindPresent(string folder, string? spec)
    {
        var token = Token(spec);

        if (token.Length == 0 || !Directory.Exists(folder))
        {
            return null;
        }

        // The leaf of "publisher/name", without a pinned quant: the publisher is
        // a folder on disk, not part of the model's name.
        var leaf = token[(token.LastIndexOf('/') + 1)..];

        if (leaf.Length == 0)
        {
            return null;
        }

        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var path in files)
        {
            var directory = Path.GetDirectoryName(path);

            var haystack = string.Join(
                ' ',
                Path.GetFileNameWithoutExtension(path),
                directory is null ? string.Empty : Path.GetFileName(directory),
                directory is null ? string.Empty : Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty));

            if (Matches(haystack, leaf))
            {
                return path;
            }
        }

        return null;
    }
}
