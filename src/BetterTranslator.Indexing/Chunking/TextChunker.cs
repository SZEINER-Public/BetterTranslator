namespace BetterTranslator.Indexing.Chunking;

/// <summary>
/// One piece of a source, as stored and embedded.
/// </summary>
public sealed record TextChunk(int Ordinal, string Text, int StartOffset, int WordCount);

public sealed record ChunkOptions
{
    /// <summary>Where a chunk wants to end.</summary>
    public int TargetCharacters { get; init; } = 900;

    /// <summary>Hard ceiling. A chunk never exceeds this.</summary>
    public int MaxCharacters { get; init; } = 1200;

    /// <summary>
    /// Carried from the end of one chunk into the start of the next, so a term
    /// sitting on a boundary is still retrievable from both sides.
    /// </summary>
    public int OverlapCharacters { get; init; } = 120;

    /// <summary>
    /// A break is only accepted past this point, so preferring a boundary
    /// cannot produce a chunk of a few characters.
    /// </summary>
    public int MinimumCharacters { get; init; } = 300;
}

/// <summary>
/// Splits text into chunks on the most natural boundary available: a paragraph
/// break first, then a sentence end, then any whitespace. A word is never split
/// in half, because a half word embeds as nonsense.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<TextChunk> Chunk(string? text, ChunkOptions? options = null)
    {
        options ??= new ChunkOptions();

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunks = new List<TextChunk>();
        var position = 0;
        var ordinal = 0;

        while (position < text.Length)
        {
            // Skip leading whitespace so a chunk never opens on a blank line.
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position >= text.Length)
            {
                break;
            }

            var remaining = text.Length - position;

            if (remaining <= options.MaxCharacters)
            {
                Add(chunks, ref ordinal, text, position, text.Length);
                break;
            }

            var end = FindBreak(text, position, options);

            Add(chunks, ref ordinal, text, position, end);

            var next = StepBack(text, end, options.OverlapCharacters);

            // Guarantee progress: an overlap that lands at or before the start
            // would loop forever.
            position = next > position ? next : end;
        }

        return chunks;
    }

    /// <summary>
    /// Best break in the window between the minimum and the ceiling, preferring
    /// a paragraph, then a sentence, then whitespace.
    /// </summary>
    private static int FindBreak(string text, int start, ChunkOptions options)
    {
        var ceiling = Math.Min(start + options.MaxCharacters, text.Length);
        var floor = Math.Min(start + options.MinimumCharacters, ceiling);
        var target = Math.Min(start + options.TargetCharacters, ceiling);

        // A paragraph break at or before the target is the cleanest cut.
        var paragraph = LastParagraphBreak(text, floor, target);
        if (paragraph > start)
        {
            return paragraph;
        }

        // Then a sentence end anywhere up to the ceiling.
        var sentence = LastSentenceEnd(text, floor, ceiling);
        if (sentence > start)
        {
            return sentence;
        }

        // Then any whitespace, which at least keeps words whole.
        for (var i = ceiling - 1; i > floor; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        // A single unbroken run longer than the ceiling: cut at the ceiling
        // rather than growing without bound.
        return ceiling;
    }

    private static int LastParagraphBreak(string text, int floor, int limit)
    {
        for (var i = limit - 1; i > floor; i--)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            // A blank line is a paragraph break; a single newline is not.
            var back = i - 1;
            while (back > floor && (text[back] == '\r' || text[back] == ' '))
            {
                back--;
            }

            if (back > floor && text[back] == '\n')
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static int LastSentenceEnd(string text, int floor, int limit)
    {
        for (var i = limit - 1; i > floor; i--)
        {
            if (text[i] is not ('.' or '!' or '?'))
            {
                continue;
            }

            var after = i + 1;
            if (after >= text.Length || char.IsWhiteSpace(text[after]))
            {
                return after;
            }
        }

        return -1;
    }

    /// <summary>
    /// Steps back by the overlap and then forward to the next word boundary, so
    /// the following chunk opens on a whole word.
    /// </summary>
    private static int StepBack(string text, int end, int overlap)
    {
        if (overlap <= 0)
        {
            return end;
        }

        var target = Math.Max(0, end - overlap);

        while (target < end && !char.IsWhiteSpace(text[target]))
        {
            target++;
        }

        return target;
    }

    private static void Add(List<TextChunk> chunks, ref int ordinal, string text, int start, int end)
    {
        var slice = text[start..end].Trim();

        if (slice.Length == 0)
        {
            return;
        }

        chunks.Add(new TextChunk(ordinal++, slice, start, CountWords(slice)));
    }

    /// <summary>
    /// Whitespace-separated tokens. This is the figure the rail shows, so it is
    /// counted once here rather than estimated per surface.
    /// </summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var words = 0;
        var inWord = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return words;
    }
}
