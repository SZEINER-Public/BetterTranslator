using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Slop;

/// <summary>A term the translation is required to carry.</summary>
/// <param name="Hint">
/// The word shown to the model, which is not always the one checked for. The
/// stem in the target column is for checking; prompting with a stem made the
/// model paste it and invent an ending, producing the wrong part of speech.
/// </param>
public sealed record GlossaryTerm(string Source, string Target, string Hint);

/// <summary>
/// A word the translation may not introduce unless the source justifies it.
/// </summary>
/// <param name="Requires">
/// A regex alternation, not a literal: one target word can be justified by any
/// of several source words -- "adresar" by "directory" or "folder" -- and
/// escaping it made every such entry unsatisfiable.
/// </param>
public sealed record BannedWord(string Word, string Requires);

public sealed record GlossaryTables(
    IReadOnlyList<GlossaryTerm> Terms,
    IReadOnlyList<BannedWord> Banned,
    IReadOnlyList<string> Keep)
{
    public static GlossaryTables Empty { get; } = new([], [], []);

    public bool IsEmpty => Terms.Count == 0 && Banned.Count == 0 && Keep.Count == 0;
}

/// <summary>
/// Terminology, which is the one thing prompting cannot fix. Ported from
/// `Import-Glossary`, `Get-GlossaryHint` and `Test-Glossary` in `rag.ps1`.
///
/// A single document rendered "provision" eight different ways -- including left
/// in English twice, and once as the legal sense of the word. Every individual
/// answer was defensible; the set was not. Consistency is a property of the
/// document, and the model only ever sees one line.
/// </summary>
public static class Glossary
{
    /// <summary>
    /// The glossary IN FORCE for a language: the reader's own file, and nothing
    /// at all until there is one.
    ///
    /// This is deliberately not <see cref="Shipped"/>, and the difference is the
    /// whole point. A glossary is a claim about ONE body of documents rather than
    /// about the language. The shipped file is the reference project's own
    /// vocabulary -- it requires "store" to become "úložiště", which is right for
    /// a model store and wrong for the shop Mr. White went to last night. Loading
    /// it as a default put that instruction into every prompt and then refused
    /// every correct answer that ignored it.
    /// </summary>
    public static GlossaryTables Active(string language = "cs")
    {
        var file = Config.ConfigStore.Find($"glossary-{language}");

        if (file is null)
        {
            return GlossaryTables.Empty;
        }

        var bytes = Config.ConfigStore.BytesFor(file.Id);

        return bytes.Length == 0
            ? GlossaryTables.Empty
            : Parse(System.Text.Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// The worked example shipped for a language, or empty when none is. Offered
    /// as a starting point in the editor; see <see cref="Active"/> for what is
    /// actually applied.
    ///
    /// Read as UTF-8 explicitly, which is not optional and is the same trap the
    /// reference records: Windows PowerShell reads a file without a byte order
    /// mark using the system ANSI codepage, so "slozka" came back as mojibake --
    /// and a glossary of mojibake rejects every correct answer.
    /// </summary>
    public static GlossaryTables Shipped(string language = "cs")
    {
        // Hyphenated, not dotted: "glossary.cs.md" reads to MSBuild as a Czech
        // satellite resource and never reaches this assembly at all.
        var resource = $"BetterTranslator.Engine.Data.glossary-{language}.md";

        using var stream = typeof(Glossary).Assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return GlossaryTables.Empty;
        }

        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses the three Markdown tables positionally.
    ///
    /// Cells are split the way Markdown does: an escaped pipe is content, not a
    /// separator. The Requires column holds regex alternations, so it needs
    /// literal pipes, and a naive split cut every one of them in half.
    /// </summary>
    public static GlossaryTables Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return GlossaryTables.Empty;
        }

        var terms = new List<GlossaryTerm>();
        var banned = new List<BannedWord>();
        var keep = new List<string>();

        var section = string.Empty;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var heading = Heading.Match(line);

            if (heading.Success)
            {
                var h = heading.Groups[1].Value.Trim();

                section = MustNotBeAdded.IsMatch(h) ? "banned"
                    : RequiredTerms.IsMatch(h) ? "terms"
                    : KeepInEnglish.IsMatch(h) ? "keep"
                    : string.Empty;

                continue;
            }

            if (section.Length == 0 || !TableRow.IsMatch(line) || SeparatorRow.IsMatch(line))
            {
                continue;
            }

            var cells = Cell.Split(line).Select(c => c.Trim().Replace(@"\|", "|", StringComparison.Ordinal)).ToArray();

            // Splitting on a leading pipe yields an empty first cell.
            cells = [.. cells.Skip(1)];

            if (section == "keep")
            {
                // One column: the term is protected as a span and never reaches
                // the model, so there is nothing to translate and nothing to
                // check. It needs no second cell, unlike the other tables.
                if (cells.Length < 1)
                {
                    continue;
                }

                var word = cells[0];

                if (word.Length == 0 || KeepHeader.IsMatch(word) || keep.Contains(word, StringComparer.Ordinal))
                {
                    continue;
                }

                keep.Add(word);
                continue;
            }

            if (cells.Length < 2 || cells[0].Length == 0 || cells[1].Length == 0 || TermHeader.IsMatch(cells[0]))
            {
                continue;
            }

            if (section == "terms")
            {
                var hint = cells.Length >= 3 && cells[2].Length > 0 ? cells[2] : cells[1];
                terms.Add(new GlossaryTerm(cells[0], cells[1], hint));
            }
            else
            {
                banned.Add(new BannedWord(cells[0], cells[1]));
            }
        }

        return new GlossaryTables(terms, banned, keep);
    }

    /// <summary>
    /// The glossary lines for one piece of text: only the terms actually
    /// present, so the prompt does not carry a dictionary the model has to read
    /// past.
    /// </summary>
    public static string Hint(GlossaryTables? glossary, string? text, string language = "Czech")
    {
        if (glossary is null || glossary.Terms.Count == 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var hits = new List<string>();

        foreach (var term in glossary.Terms)
        {
            if (!Regex.IsMatch(text, @"\b" + Regex.Escape(term.Source), RegexOptions.IgnoreCase))
            {
                continue;
            }

            var pair = $"{term.Source} = {(term.Hint.Length > 0 ? term.Hint : term.Target)}";

            if (!hits.Contains(pair, StringComparer.Ordinal))
            {
                hits.Add(pair);
            }
        }

        return hits.Count == 0
            ? string.Empty
            : $" Required {language} terminology (inflect them as the sentence needs): {string.Join("; ", hits)}.";
    }

    /// <summary>
    /// A reason when the answer breaks the glossary, or null.
    ///
    /// Two failures: a required term that is absent, and a word added that the
    /// source gives no basis for. Both are checked case-insensitively, because
    /// the target language inflects.
    /// </summary>
    public static string? Check(GlossaryTables? glossary, string source, string translated)
    {
        if (glossary is null)
        {
            return null;
        }

        // Only the translatable part of the source counts. "store" occurs in
        // `--model-store`, a protected span the model never sees and never
        // translates, so demanding the target term for it rejected correct
        // lines. A glossary term hidden inside code is not a term the
        // translation was ever asked to carry.
        var plain = source;

        foreach (var stripper in Strippers)
        {
            plain = stripper.Replace(plain, " ");
        }

        foreach (var term in glossary.Terms)
        {
            if (!Regex.IsMatch(plain, @"\b" + Regex.Escape(term.Source), RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (!Regex.IsMatch(translated, Regex.Escape(term.Target), RegexOptions.IgnoreCase))
            {
                return $"glossary term missing: '{term.Source}' must become '{term.Target}'";
            }
        }

        foreach (var word in glossary.Banned)
        {
            if (!Regex.IsMatch(translated, Regex.Escape(word.Word), RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Requires is an alternation and is deliberately NOT escaped.
            if (Regex.IsMatch(source, "(" + word.Requires + ")", RegexOptions.IgnoreCase))
            {
                continue;
            }

            return $"word added with no basis in the source: '{word.Word}' needs '{word.Requires}'";
        }

        return null;
    }

    private static readonly Regex Heading = new(@"^\s*##\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex MustNotBeAdded = new("must not be added", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RequiredTerms = new("required terms", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeepInEnglish = new("keep in English", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TableRow = new(@"^\s*\|", RegexOptions.Compiled);
    private static readonly Regex SeparatorRow = new(@"^\s*\|[\s\-:|]+\|\s*$", RegexOptions.Compiled);
    private static readonly Regex Cell = new(@"(?<!\\)\|", RegexOptions.Compiled);
    private static readonly Regex KeepHeader = new("^(term|word|english)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TermHeader = new("^(English|Czech word)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex[] Strippers =
    [
        new("`[^`]*`", RegexOptions.Compiled),
        new(@"https?://\S+", RegexOptions.Compiled),
        new(@"[A-Za-z]:\\\S+", RegexOptions.Compiled),
        new("--[a-z][a-z0-9-]*", RegexOptions.Compiled),
        new(@"\b[\w.-]+\.(?:bat|cmd|ps1|psm1|js|json|md|log|exe|dll|gguf|txt|ya?ml|xml|ini|cfg)\b", RegexOptions.Compiled),
    ];
}
