using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BetterTranslator.Engine.Markdown;

/// <summary>
/// Cuts a Markdown document into the units that may be translated, and leaves
/// everything else alone by never naming it.
///
/// The design is subtractive on purpose. Nothing is on a list of "things to
/// protect"; the only thing that travels is text that Markdig reports as a
/// literal inline inside a paragraph or a heading. A fence, a URL, an inline
/// code span, raw HTML, front matter and every syntax character in the document
/// are safe because no rule here can reach them -- not because a pattern was
/// written to exclude them. A construct nobody thought of is protected by
/// default, which is the opposite of how a regex guard fails.
/// </summary>
public static class MarkdownSegmenter
{
    public static IReadOnlyList<MarkdownUnit> Segment(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (markdown.Length == 0)
        {
            return [];
        }

        var units = new List<MarkdownUnit>();
        Collect(MarkdownSyntax.Parse(markdown), markdown, units);

        // Splicing runs back to front, so the caller is handed them in document
        // order and reverses once rather than sorting per edit.
        units.Sort((a, b) => a.Start.CompareTo(b.Start));
        return units;
    }

    private static void Collect(ContainerBlock container, string text, List<MarkdownUnit> into)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                // The two blocks that carry prose. A table cell reaches here as
                // the paragraph inside it, so cells are units of their own and a
                // row is never one long string.
                case ParagraphBlock or HeadingBlock:
                    var unit = Build((LeafBlock)block, text);
                    if (unit is not null)
                    {
                        into.Add(unit);
                    }

                    break;

                case Markdig.Extensions.Yaml.YamlFrontMatterBlock yaml
                    when Config.PipelineOptions.TranslateFrontMatterProse:
                    FrontMatter(yaml, text, into);
                    break;

                // Quotes, lists, list items, tables and their rows all hold
                // other blocks; the prose is further down.
                case Table or TableRow or TableCell or QuoteBlock or ListBlock or ListItemBlock:
                    Collect((ContainerBlock)block, text, into);
                    break;

                // Everything else -- fences, indented code, HTML, front matter,
                // thematic breaks, link reference definitions -- is passed over
                // without being read.
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ScalarField =
        new(@"^([A-Za-z0-9_.\-]+):[ \t]+(\S.*?)[ \t]*$");

    private static readonly System.Text.RegularExpressions.Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");

    private static readonly System.Text.RegularExpressions.Regex Identifier = new(@"^[\w.\-/\\]+$");

    private static readonly System.Text.RegularExpressions.Regex ValueWord =
        new(@"[\p{L}\p{Nd}]+(?:['’-][\p{L}\p{Nd}]+)*");

    private const int FrontMatterMinimumWords = 6;

    private static void FrontMatter(LeafBlock block, string text, List<MarkdownUnit> into)
    {
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var slice = block.Lines.Lines[i].Slice;

            if (slice.Length <= 0 || slice.Start < 0 || slice.Start + slice.Length > text.Length)
            {
                continue;
            }

            var match = ScalarField.Match(text.Substring(slice.Start, slice.Length));

            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups[2];

            if (!IsProseValue(value.Value))
            {
                continue;
            }

            var start = slice.Start + value.Index;

            into.Add(new MarkdownUnit(start, value.Length, value.Value, [], [new MarkdownRun(start, value.Length)]));
        }
    }

    private static bool IsProseValue(string value) =>
        value.Length > 0
        && !"[{&*|>\"'".Contains(value[0], StringComparison.Ordinal)
        && !IsoDate.IsMatch(value)
        && !Identifier.IsMatch(value)
        && ValueWord.Matches(value).Count >= FrontMatterMinimumWords;

    /// <summary>
    /// One block becomes one unit spanning its first translatable character to
    /// its last. Deliberately not the block's own span: that would swallow the
    /// `## ` of a heading or the `- ` of a list item into the unit, and those
    /// are structure. Bounded by the text, the markers stay outside and cannot
    /// be reached at all.
    /// </summary>
    private static MarkdownUnit? Build(LeafBlock block, string text)
    {
        var spans = new List<(int Start, int End)>();
        Literals(block.Inline, spans);

        if (spans.Count == 0)
        {
            return null;
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var start = spans[0].Start;
        var end = spans[^1].End;

        if (start < 0 || end >= text.Length || end < start)
        {
            // A span the parser could not place. Skipping the block leaves it in
            // the source language, which is the safe half of the trade.
            return null;
        }

        // Whitespace at either end of the prose belongs to the document, not to
        // the model. Two cases make this load-bearing rather than tidy: the
        // space after a `- [x]` marker, which a model that trims its answer
        // would delete and turn the task item back into plain text, and the two
        // trailing spaces that spell a hard line break, which nothing would ever
        // put back.
        while (start <= end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end >= start && char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        if (end < start)
        {
            return null;
        }

        spans = [.. spans
            .Select(span => (Start: Math.Max(span.Start, start), End: Math.Min(span.End, end)))
            .Where(span => span.Start <= span.End)];

        var protectedText = new StringBuilder();
        var guards = new List<MarkdownGuard>();
        var runs = new List<MarkdownRun>();
        var cursor = start;

        foreach (var (spanStart, spanEnd) in spans)
        {
            if (spanStart > cursor)
            {
                Gap(text[cursor..spanStart], protectedText, guards);
            }

            Literal(text, spanStart, spanEnd, protectedText, guards, runs);
            cursor = spanEnd + 1;
        }

        var unitText = protectedText.ToString();

        // Nothing but machinery. A cell holding one quoted citation and no prose
        // of its own has nothing to translate, and sending the sentinel alone
        // invites an answer that damages it.
        return unitText.Any(char.IsLetter)
            ? new MarkdownUnit(start, end - start + 1, unitText, guards, runs)
            : null;
    }

    private static readonly System.Text.RegularExpressions.Regex Citation =
        new("\"[^\"\\r\\n]*[^\\s\"][^\"\\r\\n]*\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// One run of prose, with anything it quotes lifted out of it.
    ///
    /// A double-quoted span in a technical document is a citation: the name of a
    /// section in another document, a string the program prints, a value to type.
    /// Translating it breaks the thing it points at, and the observed damage came
    /// in pairs, the words changed and the straight quotes replaced with
    /// typographic ones. Lifting the span with its quote characters makes both
    /// impossible, and the rest of the sentence still travels as prose.
    /// </summary>
    private static void Literal(
        string text,
        int spanStart,
        int spanEnd,
        StringBuilder into,
        List<MarkdownGuard> guards,
        List<MarkdownRun> runs)
    {
        var length = spanEnd - spanStart + 1;

        if (!Config.PipelineOptions.ProtectQuotedCitations)
        {
            into.Append(text, spanStart, length);
            runs.Add(new MarkdownRun(spanStart, length));

            return;
        }

        var span = text.Substring(spanStart, length);
        var cursor = 0;

        foreach (var citation in Citation.Matches(span).Cast<System.Text.RegularExpressions.Match>())
        {
            if (citation.Index > cursor)
            {
                var prose = citation.Index - cursor;
                into.Append(span, cursor, prose);
                runs.Add(new MarkdownRun(spanStart + cursor, prose));
            }

            Gap(citation.Value, into, guards);
            cursor = citation.Index + citation.Length;
        }

        if (cursor < span.Length)
        {
            into.Append(span, cursor, span.Length - cursor);
            runs.Add(new MarkdownRun(spanStart + cursor, span.Length - cursor));
        }
    }

    /// <summary>
    /// What sits between two runs of prose.
    ///
    /// Whitespace goes through as itself. A soft line break inside a paragraph
    /// or a list item is prose whitespace, and handing the model `while[[2]]you`
    /// instead of a line break costs a good translation for nothing: CommonMark
    /// lazy continuation means the answer reads the same whether the model keeps
    /// the break or reflows the sentence onto one line.
    ///
    /// Everything else is markup and is lifted out. The model never sees a
    /// bracket, an asterisk or a URL, so it cannot translate one, move one, or
    /// decide the sentence would read better without one.
    /// </summary>
    private static void Gap(string gap, StringBuilder into, List<MarkdownGuard> guards)
    {
        if (gap.Length == 0)
        {
            return;
        }

        if (gap.All(char.IsWhiteSpace))
        {
            into.Append(gap);
            return;
        }

        var sentinel = $"[[{guards.Count}]]";
        guards.Add(new MarkdownGuard(sentinel, gap));
        into.Append(sentinel);
    }

    /// <summary>
    /// The translatable spans under an inline container.
    ///
    /// Emphasis, links and images are walked into, so the words inside them
    /// travel with the sentence they belong to and a link's own label and an
    /// image's alt text get translated. Code spans, autolinks and raw HTML are
    /// not walked into and are not literals, so they fall into a gap and are
    /// lifted out whole.
    /// </summary>
    private static void Literals(ContainerInline? container, List<(int Start, int End)> into)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    // Measured off the document rather than read off the inline:
                    // Content has escapes and entities already resolved, and
                    // writing a resolved form back would rewrite the source.
                    if (literal.Span.Length > 0)
                    {
                        into.Add((literal.Span.Start, literal.Span.End));
                    }

                    break;

                // The label of an autolink is the URL. Walking in would offer it
                // to the model as prose.
                case LinkInline { IsAutoLink: true }:
                    break;

                case ContainerInline nested:
                    Literals(nested, into);
                    break;
            }
        }
    }
}
