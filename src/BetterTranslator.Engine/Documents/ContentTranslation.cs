using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;

namespace BetterTranslator.Engine.Documents;

public enum ContentShape
{
    Json,
    Markdown,
    Prose,
}

/// <summary>
/// What one piece of content came back as.
/// </summary>
/// <param name="Text">
/// The translation, or null when nothing usable came back. Null is not an
/// error to be worded here -- the caller owns the sentence -- but it is the
/// difference between a result and a refusal, and it is never the source text:
/// putting untranslated text under a target-language heading is the failure this
/// whole path exists to prevent.
/// </param>
/// <param name="Note">
/// What a reader would otherwise have to notice by comparing two columns word by
/// word: values that kept their source, blocks retranslated phrase by phrase.
/// Null when the run was uneventful.
/// </param>
/// <param name="Refusal">
/// Why the answer was discarded whole. Set only by the Markdown backstop, which
/// is the one failure where every block translated fine and the document still
/// could not be assembled.
/// </param>
public sealed record ContentTranslationResult(
    string? Text,
    ContentShape Shape,
    string? Note = null,
    string? Refusal = null,
    bool Stopped = false)
{
    public IReadOnlyList<Verification.Structure.SegmentTrace> Segments { get; init; } = [];

    public Core.Verification.Checks.Coverage.CompletionReport? Completion { get; init; }
}

/// <summary>
/// How a piece of text is cut up and put back together, for every surface that
/// translates one.
///
/// The routing is by content and not by file name. A .txt holding JSON is a
/// resource file; a .json holding no translatable values is prose; text typed
/// into a window has no name at all. Deciding from an extension gave the window
/// and the agent surfaces different answers for the same bytes.
///
/// Each shape knows how its own text is meant to be cut, and cutting is what
/// bounds the blast radius: ten lines sent as one blob are one answer that every
/// gate judges as a whole, so a single bad line refuses all ten. Measured on
/// exactly that -- line by line, three of four came back correct; as one block,
/// the whole thing was refused over one word.
/// </summary>
public static class ContentTranslation
{
    /// <summary>
    /// Routes, translates and reassembles. <paramref name="translate"/> is called
    /// once per unit -- a JSON batch, a Markdown block, a line or a sentence --
    /// and the caller owns what a unit costs and what context travels with it.
    /// </summary>
    public static async Task<ContentTranslationResult> TranslateAsync(
        string source,
        Func<string, CancellationToken, Task<string?>> translate,
        CancellationToken cancellationToken = default)
    {
        // JSON first: a resource file is not Markdown even where a parser would
        // accept it as such, and its values are translated one at a time rather
        // than as a document. A Markdown message then goes block by block so its
        // structure is spliced rather than regenerated. Prose takes the ordinary
        // path, and so does anything that looks structured but holds nothing to
        // send -- otherwise it would fail without ever being tried.
        if (JsonSyntax.HasTranslatableValues(source))
        {
            return Json(source, await JsonTranslation.TranslateAsync(source, translate, cancellationToken)
                .ConfigureAwait(false));
        }

        if (MarkdownSyntax.HasTranslatableProse(source))
        {
            return Markdown(
                source,
                await MarkdownTranslation.TranslateAsync(source, translate, cancellationToken)
                    .ConfigureAwait(false));
        }

        return Prose(source, await MessageTranslation.TranslateAsync(source, translate, cancellationToken)
            .ConfigureAwait(false));
    }

    public static bool IsChunked(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (JsonSyntax.HasTranslatableValues(source) || MarkdownSyntax.HasTranslatableProse(source))
        {
            return true;
        }

        return source.ReplaceLineEndings("\n").Split('\n').Count(line => line.Trim().Length > 0) > 1;
    }

    private static ContentTranslationResult Json(string source, JsonTranslationResult document) =>
        document.Translated == 0
            ? new ContentTranslationResult(null, ContentShape.Json, Stopped: document.Stopped)
            : new ContentTranslationResult(document.Text, ContentShape.Json, KeptValues(document), Stopped: document.Stopped)
            {
                Segments = document.Segments,
                Completion = Verification.Coverage.CompletionReporting.For(source, document.Text, document.Segments),
            };

    private static ContentTranslationResult Markdown(string source, MarkdownTranslationResult document)
    {
        if (!document.StructureHeld)
        {
            return new ContentTranslationResult(
                null,
                ContentShape.Markdown,
                Refusal: "the document would not have come back intact - "
                    + string.Join("; ", document.StructureIssues),
                Stopped: document.Stopped);
        }

        // Nothing usable came back for any block. Returning the source would put
        // untranslated text under the target-language heading.
        if (string.Equals(document.Text, source, StringComparison.Ordinal))
        {
            return new ContentTranslationResult(null, ContentShape.Markdown, Stopped: document.Stopped);
        }

        return new ContentTranslationResult(document.Text, ContentShape.Markdown, Recovery(document), Stopped: document.Stopped)
        {
            Segments = document.Segments,
            Completion = Verification.Coverage.CompletionReporting.For(source, document.Text, document.Segments),
        };
    }

    private static ContentTranslationResult Prose(string source, MessageTranslationResult message)
    {
        // Recovered counts too: a block rescued phrase by phrase is translated
        // text, and judging on Translated alone threw away a document where
        // every block took the recovery path.
        if (message.Translated + message.Recovered == 0)
        {
            return new ContentTranslationResult(null, ContentShape.Prose, Stopped: message.Stopped);
        }

        var note = message.Kept switch
        {
            0 => null,
            1 => "1 line kept its source",
            _ => $"{message.Kept} lines kept their source",
        };

        return new ContentTranslationResult(message.Text, ContentShape.Prose, note, Stopped: message.Stopped)
        {
            Segments = message.Segments,
            Completion = Verification.Coverage.CompletionReporting.For(source, message.Text, message.Segments),
        };
    }

    /// <summary>
    /// Names the keys that kept their source, up to a few. A resource file can
    /// have hundreds, and a note listing all of them is a note nobody reads.
    /// </summary>
    private static string? KeptValues(JsonTranslationResult document)
    {
        const int Named = 5;

        if (document.Kept == 0)
        {
            return null;
        }

        var head = string.Join(", ", document.KeptPaths.Take(Named));

        var count = document.Kept == 1
            ? "1 value kept its source"
            : $"{document.Kept} values kept their source";

        return document.Kept > Named
            ? $"{count}: {head}, and {document.Kept - Named} more"
            : $"{count}: {head}";
    }

    private static string? Recovery(MarkdownTranslationResult document)
    {
        var parts = new List<string>();

        if (document.Recovered > 0)
        {
            parts.Add(document.Recovered == 1
                ? "1 block was retranslated phrase by phrase to keep its formatting"
                : $"{document.Recovered} blocks were retranslated phrase by phrase to keep their formatting");
        }

        if (document.Kept > 0)
        {
            parts.Add(document.Kept == 1
                ? "1 block kept its source"
                : $"{document.Kept} blocks kept their source");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
