using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markdown;

/// <summary>
/// Whether an answer may be spliced back into the document, and the putting-back
/// itself.
///
/// The model is asked to do one mechanical thing besides translating: carry the
/// sentinels through, all of them, once each, in order. That is checked here
/// rather than hoped for. A dropped sentinel would delete a link; a reordered
/// pair would wrap the wrong words in bold; an invented one would leave `[[3]]`
/// sitting in the reader's text.
/// </summary>
public static class MarkdownEcho
{
    private static readonly Regex Sentinel = new(@"\[\[(\d+)\]\]", RegexOptions.Compiled);

    /// <summary>
    /// A blank line ends a block. Introducing one inside a list item would split
    /// the item, and inside a paragraph would split the paragraph -- both are
    /// structure changes the sentinel check cannot see.
    /// </summary>
    private static readonly Regex BlankLine = new(@"\n[ \t]*\n", RegexOptions.Compiled);

    /// <summary>
    /// True when the answer can be trusted with this unit's markup.
    ///
    /// Order matters as much as presence: `[[0]]bold[[1]]` and `[[1]]bold[[0]]`
    /// both contain every sentinel, and the second one closes the emphasis
    /// before it opens.
    /// </summary>
    public static bool Holds(MarkdownUnit unit, string? answer)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var found = Sentinel.Matches(answer);

        if (found.Count != unit.Guards.Count)
        {
            return false;
        }

        for (var i = 0; i < unit.Guards.Count; i++)
        {
            // The nth sentinel in the answer has to be the nth sentinel of the
            // unit. That is presence, count and order in one comparison.
            if (found[i].Value != unit.Guards[i].Sentinel)
            {
                return false;
            }
        }

        // A unit that spans lines has to come back spanning the same lines. An
        // answer that folds a two line paragraph into one loses a line from the
        // document, and every count the structural backstop makes still holds:
        // that is how one file came back thirty nine lines short. Rejecting here
        // sends the unit to the run by run path, which splices each run over its
        // own span and cannot move a line break.
        if (Breaks(answer!) != Breaks(unit.Text))
        {
            return false;
        }

        return !BlankLine.IsMatch(answer) || BlankLine.IsMatch(unit.Text);
    }

    private static int Breaks(string text) => text.Count(character => character == '\n');

    /// <summary>
    /// Puts the markup back, byte for byte. Highest index first, so `[[1]]` does
    /// not match inside `[[10]]`.
    /// </summary>
    public static string Restore(string answer, IReadOnlyList<MarkdownGuard> guards)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(guards);

        var text = answer;

        for (var i = guards.Count - 1; i >= 0; i--)
        {
            text = text.Replace(guards[i].Sentinel, guards[i].Original, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// True when a per-run answer is safe to splice. A run sits inside a line of
    /// the document, so an answer that carries a line break or a sentinel it was
    /// never given would break the line it lands in.
    /// </summary>
    public static bool RunHolds(string? answer) =>
        !string.IsNullOrWhiteSpace(answer)
        && !answer.Contains('\n', StringComparison.Ordinal)
        && !Sentinel.IsMatch(answer);
}
