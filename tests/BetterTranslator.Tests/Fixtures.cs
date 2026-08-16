using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace BetterTranslator.Tests;

/// <summary>
/// Real files on disk for the reader tests. The readers open actual documents,
/// so the fixtures are actual documents rather than stubs.
/// </summary>
internal sealed class Fixtures : IDisposable
{
    public Fixtures()
    {
        Root = Path.Combine(Path.GetTempPath(), "bt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        TextFile = Write("release-notes.txt", TextBody);
        MarkdownFile = Write("README.md", MarkdownBody);
        JsonFile = Write("strings.en.json", JsonBody);
        WordFile = BuildWord(Path.Combine(Root, "handbook.docx"));
        PdfFile = BuildPdf(Path.Combine(Root, "terms.pdf"));
    }

    public string Root { get; }

    public string TextFile { get; }

    public string MarkdownFile { get; }

    public string JsonFile { get; }

    public string WordFile { get; }

    public string PdfFile { get; }

    public const string PdfText = "Bubble keeps your workspace in sync.";

    public const string TextBody =
        "Release notes 2.4\n" +
        "\n" +
        "\n" +
        "Fixed a locked file stalling the queue.\n";

    /// <summary>
    /// An i18n resource file, indented and nested the way one arrives. The
    /// reader hands it on as written, so the indentation is part of what is
    /// being checked.
    /// </summary>
    public const string JsonBody =
        """
        {
          "app": {
            "title": "Bubble Desktop",
            "tagline": "One folder, every machine."
          },
          "actions": {
            "sync": "Sync now",
            "retry": "Retry"
          }
        }
        """;

    public const string MarkdownBody =
        """
        # Bubble Desktop

        One folder, every machine. Bubble keeps your workspace **in sync** while
        you *work*.

        ## Requirements

        - Windows 11 or macOS 14
        - 500 MB free disk space

        ```bash
        bubble sync --watch
        ```

        | Setting | Default |
        | --- | --- |
        | Watch | on |
        | Retry | 3 |
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail an otherwise green run.
        }
    }

    private string Write(string name, string body)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static string BuildWord(string path)
    {
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                Paragraph("Windows 11 handbook"),
                Paragraph("Open Settings and pin the sidebar to your workspace."),
                new Table(
                    Row("Term", "Czech"),
                    Row("settings", "nastaveni"))));
            main.Document.Save();
        }

        return path;

        static Paragraph Paragraph(string text) => new(new Run(new Text(text)));

        static TableRow Row(string a, string b) => new(
            new TableCell(Paragraph(a)),
            new TableCell(Paragraph(b)));
    }

    /// <summary>
    /// A minimal one-page PDF written by hand. Offsets in the cross-reference
    /// table are computed from the bytes actually emitted, so the file is valid
    /// rather than merely recoverable.
    /// </summary>
    private static string BuildPdf(string path) => WritePdf(path, PdfText);

    /// <summary>
    /// The same one-page PDF with a line of text per argument, each 36 points
    /// below the last. A PDF has no lines of its own -- only glyphs at
    /// coordinates -- so this is how a test states what the reader should be
    /// able to put back together.
    /// </summary>
    public static string WritePdf(string path, params string[] lines)
    {
        var runs = string.Join(
            ' ',
            lines.Select((line, i) => i == 0 ? $"({line}) Tj" : $"0 -36 Td ({line}) Tj"));

        var content = $"BT /F1 24 Tf 72 700 Td {runs} ET";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var pdf = new StringBuilder();
        var offsets = new List<int>();

        pdf.Append("%PDF-1.4\n");

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var startXref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        pdf.Append("startxref\n").Append(startXref).Append("\n%%EOF");

        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }
}
