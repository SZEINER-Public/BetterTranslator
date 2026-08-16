using System.Text;
using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Terminology;

/// <summary>
/// One term of the trade: what it is called in the target language, what it is
/// wrongly called, and why.
/// </summary>
/// <param name="Accepted">
/// The rendering that ships. Empty marks the term do-not-translate.
/// </param>
/// <param name="Wrong">
/// The renderings that are wrong in this domain. This column is what makes
/// correction possible at all: a wrong rendering can be named, a right one
/// cannot be guessed from the target text alone.
/// </param>
public sealed record DomainTerm(string Source, string Accepted, IReadOnlyList<string> Wrong, string Reason)
{
    public bool KeepInSource => Accepted.Length == 0;

    /// <summary>
    /// A capitalised term is a name and is matched exactly, so the project
    /// called `Engine` and the common noun `engine` can both be in the table and
    /// mean different things. Everything else matches case-insensitively.
    /// </summary>
    public bool IsName => Source.Length > 0 && char.IsUpper(Source[0]);
}

public sealed record DomainTermTable(IReadOnlyList<DomainTerm> Terms)
{
    public static DomainTermTable Empty { get; } = new([]);

    /// <summary>
    /// Names first. `Engine` the project and `engine` the noun both match a
    /// source that wrote the capitalised form, and the name has to correct first
    /// or the noun rewrites the span to lower case and the name finds nothing
    /// left to restore.
    /// </summary>
    public IReadOnlyList<DomainTerm> Present(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : [.. Terms.Where(t => Contains(text, t.Source, t.IsName)).OrderByDescending(t => t.IsName)];

    internal static bool Contains(string haystack, string needle, bool exactCase = false)
    {
        var comparison = exactCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var at = 0;

        while (at <= haystack.Length - needle.Length)
        {
            var hit = haystack.IndexOf(needle, at, comparison);

            if (hit < 0)
            {
                return false;
            }

            if (IsWholeWord(haystack, hit, needle.Length))
            {
                return true;
            }

            at = hit + 1;
        }

        return false;
    }

    internal static bool IsWholeWord(string text, int start, int length, bool allowInflection = true)
    {
        var before = start == 0 || !IsWordChar(text[start - 1]);
        var afterAt = start + length;

        if (!before)
        {
            // A letter before it means this is the inside of another word.
            // Matching "port" inside "import" is the failure class this guards.
            return false;
        }

        if (afterAt >= text.Length || !IsWordChar(text[afterAt]))
        {
            return true;
        }

        // Inflection is a suffix, so a Czech ending after a stem is still the
        // same word -- except for a stem too short to name one, where the tail
        // is how "nit" becomes "nitro".
        return allowInflection && char.IsLower(text[afterAt]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>
/// The software-domain vocabulary, loaded once from the embedded table.
///
/// Separate from <see cref="Slop.Glossary"/> on purpose. A glossary is a claim
/// about one body of documents and is switched on by the Memory chip; this is a
/// claim about what the words of software engineering mean in Czech at all, and
/// applies to every send. Attaching the chip layers the project's own terms on
/// top rather than replacing these.
/// </summary>
public static partial class DomainTerms
{
    private static readonly Dictionary<string, DomainTermTable> Cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*\|", RegexOptions.Compiled)]
    private static partial Regex TableRow { get; }

    [GeneratedRegex(@"^\s*\|[\s\-:|]+\|\s*$", RegexOptions.Compiled)]
    private static partial Regex SeparatorRow { get; }

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.Compiled)]
    private static partial Regex Cell { get; }

    [GeneratedRegex("^(English|term|word)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex Header { get; }

    public static DomainTermTable For(string language = "cs")
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var table = Load(language);
            Cache[language] = table;
            return table;
        }
    }

    private static DomainTermTable Load(string language)
    {
        // Hyphenated, not dotted: "domain-terms.cs.md" reads to MSBuild as a
        // Czech satellite resource and never reaches this assembly.
        var resource = $"BetterTranslator.Engine.Data.domain-terms-{language}.md";

        using var stream = typeof(DomainTerms).Assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return DomainTermTable.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return Parse(reader.ReadToEnd());
    }

    public static DomainTermTable Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return DomainTermTable.Empty;
        }

        var terms = new List<DomainTerm>();

        // Ordinal, not case-insensitive: `Engine` the source project and
        // `engine` the common noun are two rows that mean different things.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!TableRow.IsMatch(line) || SeparatorRow.IsMatch(line))
            {
                continue;
            }

            var cells = Cell.Split(line)
                .Select(c => c.Trim().Replace(@"\|", "|", StringComparison.Ordinal))
                .Skip(1)
                .ToArray();

            if (cells.Length < 2 || cells[0].Length == 0 || Header.IsMatch(cells[0]) || !seen.Add(cells[0]))
            {
                continue;
            }

            var accepted = cells[1] == "-" ? string.Empty : cells[1];
            var wrong = cells.Length >= 3
                ? cells[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            terms.Add(new DomainTerm(cells[0], accepted, wrong, cells.Length >= 4 ? cells[3] : string.Empty));
        }

        return new DomainTermTable(terms);
    }
}
