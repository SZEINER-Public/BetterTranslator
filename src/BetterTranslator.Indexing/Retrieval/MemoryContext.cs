using System.Text;

namespace BetterTranslator.Indexing.Retrieval;

/// <summary>One retrieved passage and the file it came out of.</summary>
public sealed record MemoryPassage(string Source, string Text);

/// <summary>A source line and what it was translated to, earlier in this chat.</summary>
public sealed record TranslationPair(string Source, string Result);

/// <summary>
/// Assembles the block of remembered material that goes to the model when the
/// composer's Memory chip is attached, and only then. With no chip there is no
/// block and the model sees the text alone.
/// </summary>
public static class MemoryContext
{
    /// <summary>
    /// How much remembered material may reach the prompt. The context is 4096
    /// tokens and has to hold the instruction, this block, the text and the
    /// whole answer; roughly four characters to the token, this leaves the
    /// larger part of it free. Without a cap a well-indexed project would push
    /// the text being translated out of its own context window.
    /// </summary>
    public const int BudgetCharacters = 3000;

    /// <summary>
    /// Chat memory first, then project or folder files. The earlier turns of
    /// this conversation are what someone attaching Memory usually means, and
    /// they are the material most likely to fix a term the way this chat has
    /// already been fixing it.
    ///
    /// Returns null when there is nothing to say, so an attached chip over an
    /// empty index sends the plain translation rather than an empty heading.
    /// </summary>
    public static string? Build(
        IReadOnlyList<TranslationPair> pairs,
        IReadOnlyList<MemoryPassage> passages,
        int budget = BudgetCharacters)
    {
        var block = new StringBuilder();
        var left = budget;

        // Newest first: a term settled three turns ago outranks the same term
        // settled thirty turns ago.
        var recent = pairs
            .Where(p => p.Source.Length > 0 && p.Result.Length > 0)
            .Reverse()
            .ToList();

        foreach (var pair in recent)
        {
            var line = $"- {Flatten(pair.Source)} -> {Flatten(pair.Result)}\n";

            if (line.Length > left)
            {
                break;
            }

            if (block.Length == 0)
            {
                block.Append("Earlier in this chat:\n");
                left -= "Earlier in this chat:\n".Length;
            }

            block.Append(line);
            left -= line.Length;
        }

        var wroteFiles = false;

        foreach (var passage in passages)
        {
            var text = Flatten(passage.Text);
            var entry = $"[{passage.Source}] {text}\n";

            if (entry.Length > left)
            {
                continue;
            }

            if (!wroteFiles)
            {
                // The disclaimer is on this sub-block specifically, not just on
                // the block as a whole. Passages read like documents, which is
                // exactly what makes them the part a model tries to translate --
                // measured in the reference, a chunk list with no such line came
                // back translated and pasted into the answer.
                var heading = (block.Length > 0 ? "\n" : "")
                    + "From indexed files, for meaning only - never translate or quote it:\n";

                if (heading.Length > left)
                {
                    break;
                }

                block.Append(heading);
                left -= heading.Length;
                wroteFiles = true;
            }

            block.Append(entry);
            left -= entry.Length;
        }

        return block.Length == 0 ? null : block.ToString().TrimEnd();
    }

    /// <summary>
    /// One line per item. A chunk carrying its own newlines would otherwise
    /// break the list apart and read as separate entries.
    /// </summary>
    private static string Flatten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();

        // Long enough to carry a sentence's worth of terminology, short enough
        // that one chunk cannot spend the whole budget.
        return single.Length <= 400 ? single : single[..400].TrimEnd() + "...";
    }
}
