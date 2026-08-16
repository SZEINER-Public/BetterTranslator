using System.Text.RegularExpressions;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Slop;

namespace BetterTranslator.Engine.Documents;

/// <summary>
/// Which lines of a document may be translated, and which of them belong to the
/// same paragraph. Ported from `Get-Candidates`, `Test-SameParagraph` and
/// `Get-Paragraphs` in `translate-master.ps1`.
///
/// Both halves earn their place. Sending a line at a time gives the model no
/// sentence to work with, so it renders each fragment on its own and the result
/// reads as fragments. Sending the whole document at once loses the line count,
/// and a document whose line count changed cannot be written back over the
/// original. Grouping consecutive prose lines into paragraphs is what gets
/// context without giving up structure.
/// </summary>
public static class ParagraphSegmenter
{
    private static readonly Regex Fence = new(@"^\s*```(.*)$", RegexOptions.Compiled);

    /// <summary>Anything carrying its own line-level marker stands alone.</summary>
    private static readonly Regex Standalone =
        new(@"^\s*(#{1,6}\s|[-*+]\s|\d+[.)]\s|\||```|<)", RegexOptions.Compiled);

    private static readonly Regex Blockquote = new(@"^\s*>", RegexOptions.Compiled);

    /// <summary>
    /// A finished sentence: terminal punctuation, allowing a closing quote,
    /// bracket or footnote marker after it.
    /// </summary>
    private static readonly Regex SentenceEnd =
        new(@"[.!?:;][""'”’)\]]*\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The indexes of lines worth sending.
    ///
    /// A tagged fence is skipped whole: it is code, and its language tag says so.
    /// An UNTAGGED fence is not necessarily code -- it is where a document puts
    /// an aligned listing, whose right-hand column is prose a reader of the
    /// translation needs -- so inside one, only listing lines are candidates.
    /// </summary>
    public static IReadOnlyList<int> Candidates(
        IReadOnlyList<string> lines,
        IReadOnlyList<string>? doNotTranslate = null,
        int minLetters = TranslationCandidate.DefaultMinLetters)
    {
        var candidates = new List<int>();
        var inFence = false;
        var fenceTagged = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var fence = Fence.Match(line);

            if (fence.Success)
            {
                // The tag is on the OPENING fence only; the closing one is bare.
                if (!inFence)
                {
                    fenceTagged = fence.Groups[1].Value.Trim().Length > 0;
                }

                inFence = !inFence;
                continue;
            }

            if (inFence && (fenceTagged || !SourceResidue.IsListingLine(line)))
            {
                continue;
            }

            if (TranslationCandidate.IsWorthSending(line, doNotTranslate, minLetters))
            {
                candidates.Add(i);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Groups consecutive candidates into paragraphs. A group of one is not
    /// returned: there is nothing to gain from segmenting a single line, and the
    /// caller translates it directly.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Paragraphs(
        IReadOnlyList<string> lines,
        IReadOnlyList<int> candidates)
    {
        var set = new HashSet<int>(candidates);
        var groups = new List<IReadOnlyList<int>>();
        var i = 0;

        while (i < lines.Count)
        {
            if (!set.Contains(i))
            {
                i++;
                continue;
            }

            var j = i;

            while (j + 1 < lines.Count && set.Contains(j + 1) && SameParagraph(lines[j], lines[j + 1]))
            {
                j++;
            }

            if (j > i)
            {
                groups.Add([.. Enumerable.Range(i, j - i + 1)]);
            }

            i = j + 1;
        }

        return groups;
    }

    /// <summary>
    /// Do these two lines continue one another?
    ///
    /// A listing row is a row, not a sentence that happens to wrap. Joining them
    /// destroyed the block this was written to translate: seven aligned rows
    /// became one welded paragraph, re-wrapped, with every path buried mid-line
    /// and the column gone. Each row is its own unit, exactly like a list item.
    /// </summary>
    public static bool SameParagraph(string a, string b)
    {
        if (Standalone.IsMatch(a) || Standalone.IsMatch(b))
        {
            return false;
        }

        // A wrapped line breaks MID-SENTENCE -- that is what wrapping is. A line
        // that ends in terminal punctuation is a whole sentence, so joining it to
        // the next one buys nothing: the model already had a sentence to work
        // with. What it costs is the correspondence between line and sentence.
        //
        // Measured on ten independent sentences, one per line: joined, they came
        // back as one block re-wrapped by length, so line two began mid-clause and
        // no line matched its source any more.
        //
        // The judgement is deliberately asymmetric. Splitting a genuinely wrapped
        // line that happened to end in "Mr." costs one line of context; joining
        // ten finished sentences costs the structure of the whole answer.
        if (SentenceEnd.IsMatch(a))
        {
            return false;
        }

        if (SourceResidue.IsListingLine(a) || SourceResidue.IsListingLine(b))
        {
            return false;
        }

        // A blockquote continues only into another blockquote.
        return Blockquote.IsMatch(a) == Blockquote.IsMatch(b);
    }
}
