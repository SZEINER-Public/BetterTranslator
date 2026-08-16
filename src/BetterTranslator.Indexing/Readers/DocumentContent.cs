using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Indexing.Readers;

public enum DocumentKind
{
    PlainText,
    Markdown,
    Word,
    Pdf,
    Json,
}

/// <summary>
/// What a reader pulled out of a file. <see cref="Text"/> is always the literal
/// source; <see cref="Blocks"/> is set only for Markdown, where the preview can
/// render formatted as well as source.
/// </summary>
public sealed record DocumentContent(
    DocumentKind Format,
    string Text,
    IReadOnlyList<MdBlock>? Blocks = null)
{
    /// <summary>
    /// Only Markdown offers the Formatted and Source switch. PDF, DOCX, JSON and
    /// TXT show one view and no switch.
    /// </summary>
    public bool HasRenderingSwitch => Format == DocumentKind.Markdown;

    /// <summary>
    /// A resource file's indentation and key alignment are its structure, and a
    /// proportional face hides both, so JSON sets the way a .txt does.
    /// </summary>
    public bool IsMonospace => Format is DocumentKind.PlainText or DocumentKind.Json;

    public bool HasByteOrderMark { get; init; }
}

public interface IDocumentReader
{
    bool CanRead(string path);

    Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken);
}
