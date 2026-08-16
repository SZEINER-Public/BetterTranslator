namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// TXT: monospace, no formatting, blank lines preserved exactly as written.
/// </summary>
public sealed class TextFileReader : IDocumentReader
{
    public bool CanRead(string path) =>
        Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var byteOrderMark = BetterTranslator.Engine.Text.DocumentEncoding.ByteOrderMarkAt(path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        return new DocumentContent(DocumentKind.PlainText, text) { HasByteOrderMark = byteOrderMark };
    }
}
