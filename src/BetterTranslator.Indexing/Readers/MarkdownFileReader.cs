using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// MD: carries both the literal source and the parsed block model, so the
/// preview's Formatted and Source views come from one read.
/// </summary>
public sealed class MarkdownFileReader : IDocumentReader
{
    /// <summary>
    /// The spellings the same file arrives under. Only `.md` was accepted, so a
    /// `README.markdown` could not be attached at all -- not shown as plain text,
    /// not offered the Formatted view, simply not openable.
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".md", ".markdown", ".mdown", ".mkd", ".mdx"];

    public bool CanRead(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var byteOrderMark = BetterTranslator.Engine.Text.DocumentEncoding.ByteOrderMarkAt(path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var blocks = MarkdownBlockParser.Parse(text);

        return new DocumentContent(DocumentKind.Markdown, text, blocks) { HasByteOrderMark = byteOrderMark };
    }
}
