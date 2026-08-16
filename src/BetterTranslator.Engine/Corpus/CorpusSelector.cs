using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Corpus;

/// <summary>One file the corpus will read, with the path the rules matched on.</summary>
public sealed record CorpusFile
{
    public required string FullPath { get; init; }

    /// <summary>Path relative to the root, forward slashes. What globs see.</summary>
    public required string Relative { get; init; }

    public required string Extension { get; init; }

    public required long Length { get; init; }
}

/// <summary>
/// Which files of a folder become corpus. Ported from `Get-CorpusFiles`,
/// `Test-GlobMatch` and `Convert-GlobToRegex` in `rag-extract.ps1`.
///
/// Include and exclude are glob lists rather than a hard-coded skip list,
/// because what counts as noise is per-project: one repository's `vendor/` is
/// another's source.
/// </summary>
public static class CorpusSelector
{
    /// <summary>What a reader exists for, when the caller names nothing.</summary>
    public static readonly string[] DefaultTypes = [".md", ".txt", ".json", ".php", ".docx", ".pdf"];

    /// <summary>
    /// Above this, a file is a database, a log or a bundle -- something that
    /// would be read whole into memory and produce no usable prose.
    /// </summary>
    public const double DefaultMaxFileMegabytes = 25;

    private static readonly Dictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>
    /// Translates one glob to a regex.
    ///
    /// A leading <c>**/</c> means ZERO or more directories, not one or more.
    /// Translating it to <c>.*/</c> made <c>**/vendor/**</c> fail to match
    /// <c>vendor/x.md</c>, because <c>.*/</c> requires something before the
    /// slash -- so an exclude rule that every project relies on silently indexed
    /// the whole of vendor. It only matched when vendor was nested, which is
    /// exactly the case a test with a tidy fixture tree would not have caught.
    /// </summary>
    public static string ToRegex(string glob)
    {
        var escaped = Regex.Escape(glob.Replace('\\', '/'));

        escaped = escaped.Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal);  // **/x - x at any depth, root included
        escaped = escaped.Replace(@"/\*\*", "(?:/.*)?", StringComparison.Ordinal);  // x/**  - x itself, or anything under it
        escaped = escaped.Replace(@"\*\*", ".*", StringComparison.Ordinal);         // ** anywhere else
        escaped = escaped.Replace(@"\*", "[^/]*", StringComparison.Ordinal);        // * - within one segment
        escaped = escaped.Replace(@"\?", "[^/]", StringComparison.Ordinal);

        return "^" + escaped + "$";
    }

    /// <summary>True when any glob in the list matches. An empty list matches nothing.</summary>
    public static bool Matches(string path, IReadOnlyList<string>? globs)
    {
        if (globs is null)
        {
            return false;
        }

        var normalised = path.Replace('\\', '/');

        foreach (var glob in globs)
        {
            if (string.IsNullOrEmpty(glob))
            {
                continue;
            }

            if (PatternFor(glob).IsMatch(normalised))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex PatternFor(string glob)
    {
        lock (Compiled)
        {
            if (!Compiled.TryGetValue(glob, out var pattern))
            {
                // Paths on Windows are case-insensitive, and a rule written
                // "**/Vendor/**" must not stop working because the folder on
                // disk is lower case.
                pattern = new Regex(ToRegex(glob), RegexOptions.IgnoreCase);
                Compiled[glob] = pattern;
            }

            return pattern;
        }
    }

    /// <summary>
    /// Walks a root and returns the files that pass every rule. Order is
    /// enumeration order, which is stable enough for a run to be repeatable.
    /// </summary>
    public static IReadOnlyList<CorpusFile> Select(
        string root,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        IReadOnlyList<string>? types = null,
        double maxFileMegabytes = DefaultMaxFileMegabytes)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        include = include is { Count: > 0 } ? include : ["**"];
        types ??= DefaultTypes;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var maxBytes = (long)(maxFileMegabytes * 1024 * 1024);
        var selected = new List<CorpusFile>();

        IEnumerable<string> found;

        try
        {
            found = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var path in found)
        {
            var relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (types.Count > 0 && !types.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Matches(relative, include) || Matches(relative, exclude))
            {
                continue;
            }

            long length;

            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be measured cannot be read either. Skipping
                // it costs one document; failing here costs the whole run.
                continue;
            }

            if (length > maxBytes)
            {
                continue;
            }

            selected.Add(new CorpusFile
            {
                FullPath = path,
                Relative = relative,
                Extension = extension,
                Length = length,
            });
        }

        return selected;
    }
}
