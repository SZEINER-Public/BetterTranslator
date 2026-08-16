using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// DOCX: extracted text, one line per paragraph. Tables are flattened to
/// tab-separated rows so a glossary in a table still reads as terms.
/// </summary>
public sealed class WordFileReader : IDocumentReader
{
    public bool CanRead(string path) =>
        Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken) =>
        // OpenXml is synchronous, so keep the file work off the calling thread.
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    private static DocumentContent Read(string path, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return new DocumentContent(DocumentKind.Word, string.Empty);
        }

        var text = new StringBuilder();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                    text.AppendLine(paragraph.InnerText);
                    break;

                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                        text.AppendLine(string.Join('\t', cells));
                    }

                    break;
            }
        }

        return new DocumentContent(DocumentKind.Word, text.ToString().TrimEnd('\r', '\n'));
    }
}
