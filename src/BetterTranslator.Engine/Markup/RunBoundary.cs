using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Keeps the whitespace a spliced span carried at its edges.
///
/// A run of prose between two inline spans owns the space that separates it from
/// them. Sending the span with its edges invites an answer that drops them, and
/// splicing that answer over the whole span welds the words onto the markup
/// either side. So the edges are never sent and never replaced: the run is
/// narrowed to the text itself and only that range is spliced, which makes the
/// spacing a property of the range rather than of the answer.
/// </summary>
public static class RunBoundary
{
    private static readonly Regex Leading = new(@"^[ \t]*", RegexOptions.Compiled);
    private static readonly Regex Trailing = new(@"[ \t]*$", RegexOptions.Compiled);

    public static (int Start, int Length) Inner(string markdown, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (!Config.PipelineOptions.PreserveRunBoundaries)
        {
            return (start, length);
        }

        var span = markdown.Substring(start, length);
        var lead = Leading.Match(span).Length;
        var trail = Trailing.Match(span).Length;

        return lead + trail >= length ? (start, length) : (start + lead, length - lead - trail);
    }

    public static bool Lost(string sourceSpan, string answer)
    {
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(answer);

        return Leading.Match(sourceSpan).Length != Leading.Match(answer).Length
            || Trailing.Match(sourceSpan).Length != Trailing.Match(answer).Length;
    }

    public static string Preserve(string sourceSpan, string answer)
    {
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(answer);

        var body = answer.Trim();

        return body.Length == 0
            ? answer
            : Leading.Match(sourceSpan).Value + body + Trailing.Match(sourceSpan).Value;
    }
}
