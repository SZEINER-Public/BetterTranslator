using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace BetterTranslator.Indexing.Readers;

/// <summary>
/// PDF: extracted text, page by page.
///
/// D1, unconfirmed: text and embedded images only, no page rasterization.
/// PdfPig does not rasterize, and adding that would mean a PDFium binding.
/// </summary>
public sealed class PdfFileReader : IDocumentReader
{
    public bool CanRead(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    private static DocumentContent Read(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);

        var text = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Letter order in the file is not reading order, so group into words
            // rather than concatenating raw glyphs.
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);

            foreach (var line in Lines(words))
            {
                text.AppendLine(line);
            }
        }

        return new DocumentContent(DocumentKind.Pdf, text.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// The words of one page, back in lines.
    ///
    /// A PDF has no lines -- it has glyphs at coordinates -- so this reads them
    /// off the geometry: words sharing a baseline are a line, and a baseline
    /// that moves by more than half the text's own height starts the next one.
    /// The tolerance comes from the page rather than being a constant, because
    /// the whole point is to work at any font size.
    ///
    /// It used to join every word on a page into a single line. That is one
    /// "line" per page for everything downstream: a translator that works line
    /// by line was handed a whole page, failed it, and recovered sentence by
    /// sentence -- correct output, by the slowest possible route -- and anything
    /// that chunks or indexes by line saw a page as an indivisible unit.
    /// </summary>
    private static IEnumerable<string> Lines(IEnumerable<Word> words)
    {
        var placed = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        if (placed.Count == 0)
        {
            yield break;
        }

        // Half the median word height: tall enough that a subscript or a comma
        // sitting slightly low stays on its line, short enough that the next
        // line of the same paragraph starts a new one.
        var heights = placed.Select(w => w.BoundingBox.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        var tolerance = heights.Count == 0 ? 1 : heights[heights.Count / 2] / 2;

        var line = new List<Word>();
        var baseline = placed[0].BoundingBox.Bottom;

        foreach (var word in placed)
        {
            if (line.Count > 0 && Math.Abs(word.BoundingBox.Bottom - baseline) > tolerance)
            {
                yield return Join(line);

                line.Clear();
                baseline = word.BoundingBox.Bottom;
            }

            line.Add(word);
        }

        if (line.Count > 0)
        {
            yield return Join(line);
        }
    }

    private static string Join(List<Word> line) =>
        string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
}
