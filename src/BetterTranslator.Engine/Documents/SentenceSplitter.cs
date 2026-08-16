namespace BetterTranslator.Engine.Documents;

/// <summary>
/// Cuts a line into sentences, reporting where each one sits in it.
///
/// Exists because a line is not always a sentence. Measured on a real message:
/// a 277 character line carrying four sentences and a dozen product names came
/// back from the model unchanged and was refused whole, so the reader saw a
/// quarter of their message left in the source language with no way to tell
/// why. Split, the same line translates -- each sentence is short enough to be
/// answered, and a refusal costs one sentence rather than a paragraph.
///
/// Not a general-purpose sentence tokenizer, and deliberately not: it splits
/// only where a split is unambiguous, and leaving two sentences joined is a
/// worse translation while splitting `.NET` in half is a broken one.
/// </summary>
public static class SentenceSplitter
{
    /// <summary>
    /// The spans of each sentence in the line, trimmed of the whitespace
    /// between them so the separators stay in the document and only the
    /// sentences are replaced.
    ///
    /// A line with nothing to split on comes back as one span covering it, so
    /// the caller has one shape to handle rather than two.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Split(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var spans = new List<(int Start, int Length)>();
        var start = 0;

        for (var i = 0; i < line.Length; i++)
        {
            if (!IsTerminator(line[i]) || !Breaks(line, i))
            {
                continue;
            }

            // The terminator belongs to the sentence it ends.
            var end = i + 1;
            Add(spans, line, start, end);

            start = end;
        }

        Add(spans, line, start, line.Length);

        return spans.Count > 0 ? spans : [(0, line.Length)];
    }

    private static bool IsTerminator(char character) => character is '.' or '!' or '?';

    /// <summary>
    /// Whether this terminator really ends a sentence.
    ///
    /// Three things have to hold, and each one was put here by a case that
    /// broke without it: whitespace has to follow, or `.NET` and `i18n.json`
    /// split in half; something that can open a sentence has to follow the
    /// whitespace, or a trailing `etc. )` splits; and the character before must
    /// not be another terminator already handled, so `...` cuts once.
    /// </summary>
    private static bool Breaks(string line, int index)
    {
        var next = index + 1;

        if (next >= line.Length || !char.IsWhiteSpace(line[next]))
        {
            return false;
        }

        while (next < line.Length && char.IsWhiteSpace(line[next]))
        {
            next++;
        }

        if (next >= line.Length)
        {
            // Trailing punctuation at the end of the line: the last span covers
            // it already.
            return false;
        }

        if (!char.IsUpper(line[next]) && !char.IsDigit(line[next]) && line[next] is not ('"' or '\'' or '<'))
        {
            return false;
        }

        // A single capital before the dot is an initial -- "J. Smith" -- not the
        // end of a sentence.
        if (index >= 2 && char.IsUpper(line[index - 1]) && !char.IsLetterOrDigit(line[index - 2]))
        {
            return false;
        }

        return true;
    }

    private static void Add(List<(int Start, int Length)> spans, string line, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        if (end > start)
        {
            spans.Add((start, end - start));
        }
    }
}
