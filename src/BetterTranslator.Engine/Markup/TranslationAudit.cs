using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

public enum ProtectedCategory
{
    None,
    HtmlComment,
    FencedCode,
    InlineCode,
    TableDelimiter,
    LinkTarget,
    IdentifierShape,
    ProperNounShape,
    ShortOverlap,
    QuotedSectionName,
}

public sealed record SurvivingSpan(
    int Line,
    string Text,
    string Unit,
    bool Preserved,
    ProtectedCategory Category,
    int RunLength);

public sealed record CoverageMeasure(
    int TranslatableTokens,
    int ProtectedTokens,
    int SurvivingTokens)
{
    public int TranslatedTokens => TranslatableTokens - SurvivingTokens;

    public double Percent => TranslatableTokens == 0
        ? 0
        : 100.0 * TranslatedTokens / TranslatableTokens;
}

public sealed record StructuralFinding(int Line, string Kind, string Detail);

public sealed record AuditReport(
    CoverageMeasure Coverage,
    IReadOnlyList<SurvivingSpan> Spans,
    IReadOnlyList<StructuralFinding> Structural,
    int SourceUnits,
    int TargetUnits);

/// <summary>
/// Measures a translated document against its source without knowing either
/// language.
///
/// Every judgement here is positional or shape-based. A token still in the source
/// language is one the source also holds; whether that is correct is decided by
/// the container it sits in and the shape of the token, never by a word list.
/// </summary>
public static class TranslationAudit
{
    private const int ResidueRunWords = 4;

    private static readonly Regex Fence = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new(@"^\s*<!--.*-->\s*$", RegexOptions.Compiled);
    private static readonly Regex TableDelimiter = new(@"^\s*\|[\s\-:|]+\|\s*$", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`[^`]*`", RegexOptions.Compiled);
    private static readonly Regex LinkTarget = new(@"\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"""[^""\r\n]*""", RegexOptions.Compiled);
    private static readonly Regex Token =
        new(@"[\p{L}\p{N}](?:[\p{L}\p{N}\-'./\\:%_]*[\p{L}\p{N}])?", RegexOptions.Compiled);
    private static readonly Regex BareUrl = new(@"[a-z][a-z0-9+.\-]*://\S+", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"[./\\_<>=]|^[a-z]+[A-Z]", RegexOptions.Compiled);
    private static readonly Regex Ascii = new(@"^[\x00-\x7F]+$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*([-*+]|\d+[.)])\s", RegexOptions.Compiled);

    public static AuditReport Run(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var sourceWords = WordSet(source);
        var lines = target.Replace("\r\n", "\n").Split('\n');

        var spans = new List<SurvivingSpan>();
        var structural = new List<StructuralFinding>();

        int translatable = 0, protectedTokens = 0, surviving = 0;
        var inFence = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var number = index + 1;

            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                protectedTokens += Token.Matches(line).Count;
                continue;
            }

            if (inFence)
            {
                protectedTokens += Token.Matches(line).Count;
                continue;
            }

            if (HtmlComment.IsMatch(line))
            {
                protectedTokens += Token.Matches(line).Count;

                spans.Add(new SurvivingSpan(
                    number, line.Trim(), Unit(line), true, ProtectedCategory.HtmlComment, 0));

                continue;
            }

            if (TableDelimiter.IsMatch(line))
            {
                continue;
            }

            var masked = Mask(line, out var maskedTokens);
            protectedTokens += maskedTokens;

            var tokens = Token.Matches(masked).Cast<Match>().ToList();
            translatable += tokens.Count;

            foreach (var run in Runs(tokens, sourceWords))
            {
                var text = string.Join(' ', run.Select(token => token.Value));
                var length = run.Count;

                // One shared token proves nothing: a target-language function
                // word can spell the same as a source one. A run of them is the
                // signature of text that was never translated, unless every
                // token in it carries a name's shape, which is what a list of
                // technologies looks like in any language.
                if (length >= ResidueRunWords && !run.All(token => Named(token.Value)))
                {
                    surviving += length;

                    spans.Add(new SurvivingSpan(
                        number, text, Unit(line), false, ProtectedCategory.None, length));

                    continue;
                }

                var category = run.Count == 1
                    ? CategoryFor(run[0].Value)
                    : run.All(token => Named(token.Value))
                        ? ProtectedCategory.ProperNounShape
                        : ProtectedCategory.ShortOverlap;

                spans.Add(new SurvivingSpan(
                    number,
                    text,
                    Unit(line),
                    true,
                    category == ProtectedCategory.None ? ProtectedCategory.ShortOverlap : category,
                    length));
            }

            if (EmphasisHygiene.Damaged(line))
            {
                structural.Add(new StructuralFinding(number, "emphasis", "whitespace inside an emphasis run"));
            }

            foreach (var code in InlineCode.Matches(line).Cast<Match>())
            {
                var before = code.Index > 0 ? line[code.Index - 1] : ' ';
                var afterIndex = code.Index + code.Length;
                var after = afterIndex < line.Length ? line[afterIndex] : ' ';

                if (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after))
                {
                    structural.Add(new StructuralFinding(
                        number, "boundary", "no space between prose and an inline code span"));

                    break;
                }
            }
        }

        return new AuditReport(
            new CoverageMeasure(translatable, protectedTokens, surviving),
            spans,
            structural,
            UnitCount(source),
            UnitCount(target));
    }

    public static IReadOnlyList<string> QuotedLiteralsLost(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return
        [
            .. Quoted.Matches(source)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .Where(literal => Token.IsMatch(literal) && !target.Contains(literal, StringComparison.Ordinal))
        ];
    }

    public static int UnitCount(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var count = 0;
        var inFence = false;

        foreach (var line in lines)
        {
            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                count++;
                continue;
            }

            if (inFence || line.Trim().Length == 0)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    public static int TableRowCount(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Count(line => line.TrimStart().StartsWith('|'));
    }

    private static string Unit(string line)
    {
        if (Heading.IsMatch(line))
        {
            return "heading";
        }

        if (line.TrimStart().StartsWith('|'))
        {
            return "table cell";
        }

        if (ListItem.IsMatch(line))
        {
            return "list item";
        }

        return line.TrimStart().StartsWith('>') ? "blockquote" : "paragraph";
    }

    private static bool Named(string token) =>
        char.IsUpper(token[0]) || char.IsDigit(token[0]) || Identifier.IsMatch(token);

    private static ProtectedCategory CategoryFor(string token)
    {
        if (Identifier.IsMatch(token))
        {
            return ProtectedCategory.IdentifierShape;
        }

        return char.IsUpper(token[0]) ? ProtectedCategory.ProperNounShape : ProtectedCategory.None;
    }

    private static string Mask(string line, out int maskedTokens)
    {
        var masked = line;
        maskedTokens = 0;

        foreach (var pattern in new[] { InlineCode, LinkTarget, BareUrl, Quoted })
        {
            foreach (var match in pattern.Matches(masked).Cast<Match>())
            {
                maskedTokens += Token.Matches(match.Value).Count;
            }

            masked = pattern.Replace(masked, hole => new string(' ', hole.Length));
        }

        return masked;
    }

    private static List<List<Match>> Runs(List<Match> tokens, HashSet<string> sourceWords)
    {
        var runs = new List<List<Match>>();
        var current = new List<Match>();

        foreach (var token in tokens)
        {
            if (sourceWords.Contains(token.Value) && Ascii.IsMatch(token.Value))
            {
                current.Add(token);
                continue;
            }

            if (current.Count > 0)
            {
                runs.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    private static HashSet<string> WordSet(string markdown)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in Token.Matches(markdown).Cast<Match>())
        {
            words.Add(match.Value);
        }

        return words;
    }

    public static int ResidueThreshold => ResidueRunWords;
}
