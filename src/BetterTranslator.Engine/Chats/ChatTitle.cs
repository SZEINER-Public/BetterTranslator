using System.Text;
using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Chats;

/// <summary>
/// The instruction that asks a model to name a chat, and the test of whether
/// what came back is a name.
///
/// Both live here because the installed models are translation fine-tunes. A
/// model trained to translate answers a summarize instruction by translating it,
/// or by translating the message, or by saying nothing useful -- so the
/// validator is not tidying, it is the test that decides whether the answer is
/// used at all. Everything it cannot repair with certainty it rejects, and the
/// caller falls back rather than shipping a repaired guess into the sidebar.
/// </summary>
public static class ChatTitle
{
    public const int MaxWords = 5;

    /// <summary>
    /// What the sidebar row actually fits before it trims.
    ///
    /// Measured on screen rather than derived: "Publikování spustitelného
    /// souboru" rendered as "Publikování spustitelného sou..." at the row's
    /// resting width, which is twenty-nine characters. A name the row has to
    /// ellipsize is a name the reader cannot read, and the model can just as
    /// easily be asked for a shorter one.
    ///
    /// Deliberately shorter than the forty-character provisional cut. That one
    /// is a truncation of a sentence and reads as one; this is a title, and a
    /// title with dots after it reads as a failure.
    /// </summary>
    public const int MaxLength = 29;

    private static readonly Regex Sentinel = new(@"\[\[\d+\]\]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly char[] Wrappers = ['"', '\'', '`', '“', '”', '‘', '’', '*', '_', '#'];

    /// <summary>
    /// Labels a model writes in front of the answer instead of answering. Kept
    /// as a closed list rather than "any word before a colon", because a title
    /// may legitimately contain one.
    /// </summary>
    private static readonly string[] Labels =
        ["title", "chat title", "summary", "name", "nazev", "název", "nadpis", "shrnuti", "shrnutí"];

    /// <summary>
    /// The character budget is stated as well as the word count. Asking for a
    /// short title is cheaper than cutting a long one, and a cut lands on a word
    /// boundary that the model would have chosen better.
    /// </summary>
    public static string Instruction(string targetLanguage) =>
        $"Name this conversation in {targetLanguage}. "
        + $"Reply with a title of at most {MaxWords} words and {MaxLength} characters, "
        + $"in {targetLanguage}, and nothing else: "
        + "no quotes, no label, no punctuation at the end, no explanation.";

    /// <summary>
    /// The title in <paramref name="answer"/>, or null when there is not one.
    ///
    /// <paramref name="source"/> is the message being named, and it is here to
    /// catch the translation model's favourite failure: answering with the
    /// message itself. A first-five-words echo would otherwise be stored as a
    /// settled name and stop the chat ever being named properly.
    /// </summary>
    public static string? Clean(string? answer, string source)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var text = Sentinel.Replace(answer, " ");
        text = Whitespace.Replace(text, " ").Trim();
        text = StripLabel(text);
        text = text.Trim(Wrappers).Trim();
        text = text.TrimEnd('.', ',', ';', ':', '!', '…').Trim();
        text = text.Trim(Wrappers).Trim();

        if (text.Length < 2 || !text.Any(char.IsLetter))
        {
            return null;
        }

        text = Cap(text);

        return text.Length < 2 || EchoesSource(text, source) ? null : text;
    }

    private static string StripLabel(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0 || colon > 24 || colon == text.Length - 1)
        {
            return text;
        }

        var head = text[..colon].Trim().Trim(Wrappers).Trim();

        return Labels.Any(l => string.Equals(l, head, StringComparison.OrdinalIgnoreCase))
            ? text[(colon + 1)..].Trim()
            : text;
    }

    /// <summary>Five words, then forty characters, and the character cut lands between words.</summary>
    private static string Cap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > MaxWords)
        {
            words = words[..MaxWords];
        }

        var built = new StringBuilder();

        foreach (var word in words)
        {
            var next = built.Length == 0 ? word.Length : built.Length + 1 + word.Length;

            if (next > MaxLength)
            {
                break;
            }

            if (built.Length > 0)
            {
                built.Append(' ');
            }

            built.Append(word);
        }

        // A single word longer than the cap leaves nothing, and a hard cut is
        // better than no title at all.
        return built.Length > 0 ? built.ToString() : text[..Math.Min(MaxLength, text.Length)].Trim();
    }

    /// <summary>
    /// Compared on letters and digits alone, because the boundary between a
    /// title and the message it was cut from does not fall on a word: a title of
    /// "Default output naming: &lt;name&gt;" against a source continuing
    /// "&lt;name&gt;.&lt;to&gt;.&lt;ext&gt;" is the same words with a different
    /// last token, and word-by-word comparison calls that a different string.
    /// </summary>
    private static bool EchoesSource(string title, string source)
    {
        var head = Letters(title);

        return head.Length > 0 && Letters(source).StartsWith(head, StringComparison.OrdinalIgnoreCase);
    }

    private static string Letters(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit));
}
