namespace BetterTranslator.Runtime.Agents;

public enum DocumentFormat
{
    Json,
    Markdown,
    Text,
    Unsupported,
}

public static class DocumentFormats
{
    /// <summary>
    /// Formats that are not text on disk but have a reader that turns them into
    /// text -- the same readers the window uses, so both surfaces see the same
    /// words. They can be read and not written: nothing here produces a PDF, so
    /// the translation is written out as text under a name that says so.
    /// </summary>
    private static readonly HashSet<string> Extracted = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx",
    };

    private static readonly HashSet<string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".odt", ".rtf", ".xlsx", ".xls", ".pptx", ".ppt",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".png", ".jpg", ".jpeg", ".gif", ".bmp",
        ".webp", ".ico", ".mp3", ".mp4", ".wav", ".avi", ".mkv", ".exe", ".dll", ".gguf",
    };

    public static DocumentFormat Of(string path)
    {
        var extension = Path.GetExtension(path);

        if (Binary.Contains(extension))
        {
            return DocumentFormat.Unsupported;
        }

        return extension.ToLowerInvariant() switch
        {
            ".json" => DocumentFormat.Json,
            ".md" or ".markdown" => DocumentFormat.Markdown,
            _ => DocumentFormat.Text,
        };
    }

    /// <summary>
    /// This file has to go through a reader before there is any text to
    /// translate.
    /// </summary>
    public static bool NeedsReader(string path) => Extracted.Contains(Path.GetExtension(path));

    public static bool IsTranslatable(string path) =>
        Of(path) != DocumentFormat.Unsupported
        && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    public static string UnsupportedMessage(string path) =>
        $"{Path.GetFileName(path)} is a format this surface has no reader for. "
        + "Export it to PDF, Word, Markdown, JSON or plain text first, then translate that file.";

    public static string OutputPathFor(string input, string targetCode, string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return Path.GetFullPath(chosen);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(input)) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(input);
        var extension = Path.GetExtension(input);

        // An extracted format keeps its own extension in the name and gains a
        // text one. Naming a translated PDF .pdf would produce a file no reader
        // can open, and dropping the .pdf would collide with the .docx of the
        // same name sitting beside it in a batch.
        return Extracted.Contains(extension)
            ? Path.Combine(folder, $"{stem}{extension}.{targetCode}.txt")
            : Path.Combine(folder, $"{stem}.{targetCode}{extension}");
    }

    public static bool LooksBinary(string text) => text.Contains('\0', StringComparison.Ordinal);
}
