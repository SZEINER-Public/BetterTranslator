namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// JSON: the literal source, monospaced, nothing reformatted. A resource file is
/// translated value by value against the bytes it arrived as -- keys are never
/// sent and punctuation is spliced back from the input -- so reparsing it here
/// into a shape the writer would have to undo would only lose the file's own
/// layout.
/// </summary>
public sealed class JsonFileReader : IDocumentReader
{
    public bool CanRead(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public async Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var byteOrderMark = BetterTranslator.Engine.Text.DocumentEncoding.ByteOrderMarkAt(path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        return new DocumentContent(DocumentKind.Json, text) { HasByteOrderMark = byteOrderMark };
    }
}
