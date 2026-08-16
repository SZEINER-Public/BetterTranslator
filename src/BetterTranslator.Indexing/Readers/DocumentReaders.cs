namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// Picks the reader for a path. The formats here are exactly the ones the file
/// dialog offers.
/// </summary>
public sealed class DocumentReaders
{
    private readonly IDocumentReader[] _readers =
    [
        new TextFileReader(),
        new MarkdownFileReader(),
        new JsonFileReader(),
        new WordFileReader(),
        new PdfFileReader(),
    ];

    /// <summary>
    /// Extensions the attach dialog filters to. Built from the readers rather
    /// than listed beside them, so a spelling a reader accepts can never be one
    /// the dialog refuses to show.
    /// </summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".pdf", ".docx", .. MarkdownFileReader.Extensions, ".json", ".txt"];

    public bool IsSupported(string path) => _readers.Any(r => r.CanRead(path));

    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var reader = _readers.FirstOrDefault(r => r.CanRead(path))
            ?? throw new NotSupportedException(
                $"No reader for {Path.GetExtension(path)}. Supported: {string.Join(", ", SupportedExtensions)}.");

        return reader.ReadAsync(path, cancellationToken);
    }
}
