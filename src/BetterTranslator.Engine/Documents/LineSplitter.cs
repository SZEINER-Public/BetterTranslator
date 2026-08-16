namespace BetterTranslator.Engine.Documents;

/// <summary>
/// Splits one translated paragraph back into the exact number of lines it came
/// from. Ported from `Split-ToLines` in `translate-master.ps1`.
///
/// This is what makes paragraph translation safe. The model is given several
/// lines as one piece of prose so it has a sentence to work with, and answers
/// with one piece of prose; the document needs its line count back, because a
/// document whose line count changed cannot be written over the original.
/// </summary>
public static class LineSplitter
{
    /// <summary>
    /// Splits <paramref name="text"/> into exactly <paramref name="count"/>
    /// lines, balanced by length. Null when it cannot be done -- fewer words than
    /// lines -- so the caller falls back to translating line by line rather than
    /// writing something it invented.
    /// </summary>
    /// <param name="prefix">
    /// Re-applied to every line. A blockquote's "&gt; " belongs to each line, not
    /// to the paragraph, and a paragraph that lost it on lines two onward stops
    /// being a blockquote halfway through.
    /// </param>
    public static IReadOnlyList<string>? Split(string text, int count, string prefix = "")
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (count < 1 || words.Length < count)
        {
            return null;
        }

        if (count == 1)
        {
            return [prefix + string.Join(' ', words)];
        }

        // Balanced by character length rather than by word count: the lines came
        // from a wrapped paragraph, so equal length is what puts them back
        // looking like the original.
        var target = (int)Math.Ceiling((double)text.Length / count);
        var lines = new List<string>(count);
        var used = 0;

        for (var k = 0; k < count; k++)
        {
            // Every remaining line still needs at least one word, so this line
            // may not take them all.
            var linesLeft = count - k;
            var maxTake = Math.Max(1, words.Length - used - (linesLeft - 1));

            var take = 0;
            var length = 0;

            while (take < maxTake)
            {
                var word = words[used + take];
                var added = take == 0 ? word.Length : word.Length + 1;

                if (take > 0 && length + added > target)
                {
                    break;
                }

                length += added;
                take++;
            }

            take = Math.Max(1, take);

            lines.Add(prefix + string.Join(' ', words.Skip(used).Take(take)));
            used += take;
        }

        // Anything left over is appended to the last line rather than dropped.
        // Losing words is the one outcome worse than an uneven wrap.
        if (used < words.Length)
        {
            lines[count - 1] = lines[count - 1] + ' ' + string.Join(' ', words.Skip(used));
        }

        return lines;
    }
}
