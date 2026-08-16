using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Documents;

/// <summary>
/// Looking at one word without losing the line around it. Ported from
/// `Get-Masked` and `Get-Window` in `translate-words.ps1`.
///
/// A word-level pass is the last thing a document translation does: whatever
/// survived every earlier pass is looked at one word at a time. Both halves here
/// exist because a word on its own is not answerable -- the model needs the
/// sentence to choose a case and a gender, and the offsets have to survive
/// whatever was hidden from it.
/// </summary>
public static class WordContext
{
    private static readonly Regex NonSpace = new(@"\S+", RegexOptions.Compiled);

    /// <summary>
    /// Blanks out everything matching <paramref name="protect"/>, replacing each
    /// match with spaces of the SAME length.
    ///
    /// Length-preserving rather than removing: every index taken from the masked
    /// text is used against the original, so a mask that shortened the line would
    /// point every later match at the wrong characters.
    /// </summary>
    public static string Mask(string? text, Regex protect)
    {
        ArgumentNullException.ThrowIfNull(protect);

        return string.IsNullOrEmpty(text)
            ? text ?? string.Empty
            : protect.Replace(text, match => new string(' ', match.Value.Length));
    }

    /// <summary>
    /// The few words either side of a span, with the span itself replaced by
    /// <c>___</c>.
    ///
    /// The blank is what makes this answerable. Handed the word alone, a model
    /// translates a dictionary entry; handed the slot, it translates what belongs
    /// in that slot, which is the case, number and gender the sentence requires.
    /// </summary>
    public static string Window(string line, int index, int length, int words = 6)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (index < 0 || length < 0 || index + length > line.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "the span must lie inside the line");
        }

        var lead = NonSpace.Matches(line[..index]).TakeLast(words).Select(m => m.Value);
        var tail = NonSpace.Matches(line[(index + length)..]).Take(words).Select(m => m.Value);

        return (string.Join(' ', lead) + " ___ " + string.Join(' ', tail)).Trim();
    }
}
