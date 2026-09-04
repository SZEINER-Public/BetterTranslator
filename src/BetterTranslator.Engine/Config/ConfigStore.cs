using System.Text.Json;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Config;

/// <summary>How a configuration file is written, which decides how it is checked.</summary>
public enum ConfigFormat
{
    Json,

    /// <summary>Markdown tables -- the glossary.</summary>
    Markdown,
}

/// <summary>One editable configuration file, and where its shipped copy lives.</summary>
public sealed record ConfigFile(string Id, string DisplayName, string Description, string FileName, string Resource)
{
    /// <summary>
    /// A property the document must carry to be usable. Checked on save, because
    /// a file that parses as JSON but holds nothing the engine reads would be
    /// accepted here and fail silently much later -- an empty registry offers no
    /// languages and reports no error.
    /// </summary>
    public required string RequiredArray { get; init; }

    public ConfigFormat Format { get; init; } = ConfigFormat.Json;

    /// <summary>
    /// False when the shipped copy is a WORKED EXAMPLE rather than a default in
    /// force. Nothing is loaded from it until the reader saves a copy of their
    /// own.
    ///
    /// The glossary is the one file this applies to, and the reason is that a
    /// glossary is a claim about one body of documents rather than about the
    /// language. Shipping the reference project's own vocabulary as an active
    /// default meant every user's text was told that "store" must become
    /// "úložiště" -- so "Mr. White went to the store last night" was pushed
    /// towards a data store in the prompt and then refused for not obeying.
    /// </summary>
    public bool AppliesWhenShipped { get; init; } = true;
}

/// <summary>What a save attempt decided.</summary>
public sealed record ConfigVerdict(bool Accepted, string Detail);

/// <summary>
/// The engine's configuration, as a shipped default with a copy the reader may
/// edit on top of it.
///
/// The reference engine's own design says "adding a language is a JSON edit", so
/// the files have to be editable. They ship embedded in the assembly, which makes
/// them tamper-proof and always present but not editable, so a user copy on disk
/// takes precedence when there is one. Nothing is ever written over the shipped
/// copy: resetting is deleting the user's file, which cannot fail halfway.
/// </summary>
public sealed class ConfigStore(string folder)
{
    public static IReadOnlyList<ConfigFile> Files { get; } =
    [
        new("languages",
            "Languages",
            "",
            "languages.json",
            "BetterTranslator.Engine.Data.languages.json")
        { RequiredArray = "languages" },

        new("model-prompts",
            "Model prompts",
            "",
            "model-prompts.json",
            "BetterTranslator.Engine.Data.model-prompts.json")
        { RequiredArray = "models" },

        new("prompts",
            "App prompts",
            "",
            "prompts.json",
            "BetterTranslator.Engine.Data.prompts.json")
        { RequiredArray = "fragments" },

        new("rag-defaults",
            "Terminology",
            "",
            "rag.defaults.json",
            "BetterTranslator.Engine.Data.rag.defaults.json")
        { RequiredArray = "types" },

        new("glossary-cs",
            "Czech glossary",
            "",
            "glossary-cs.md",
            "BetterTranslator.Engine.Data.glossary-cs.md")
        {
            RequiredArray = string.Empty,
            Format = ConfigFormat.Markdown,
            AppliesWhenShipped = false,
        },
    ];

    public string Folder => folder;

    /// <summary>
    /// The store the registries read through, set once at startup before
    /// anything loads. Null means "shipped copies only", which is what a test
    /// and any consumer that never configures a folder both get.
    ///
    /// Static for the same reason BackendCatalog's search paths are: the
    /// registries are constructed in a dozen places and threading a store
    /// through all of them would put a parameter on every call site to serve one
    /// decision made once.
    /// </summary>
    public static ConfigStore? Active { get; private set; }

    public static void UseFolder(string folder) => Active = new ConfigStore(folder);

    /// <summary>
    /// The bytes to load for a file, through the active store where there is
    /// one. The registries call this rather than reaching for the resource
    /// directly, so an edited file is what they read.
    /// </summary>
    public static byte[] BytesFor(string id)
    {
        var file = Find(id);

        if (file is null)
        {
            return [];
        }

        // A file whose shipped copy is only an example loads nothing until the
        // reader has saved one of their own.
        if (!file.AppliesWhenShipped)
        {
            var own = Active?.ReadRaw(file);

            return own is null ? [] : System.Text.Encoding.UTF8.GetBytes(own);
        }

        return Active?.CurrentBytes(file) ?? System.Text.Encoding.UTF8.GetBytes(Shipped(file));
    }

    public static ConfigFile? Find(string id) =>
        Files.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    public string PathFor(ConfigFile file) => Path.Combine(folder, file.FileName);

    /// <summary>True once the reader has saved a copy of their own.</summary>
    public bool IsEdited(ConfigFile file) => File.Exists(PathFor(file));

    /// <summary>The shipped text, always available.</summary>
    public static string Shipped(ConfigFile file)
    {
        using var stream = typeof(ConfigStore).Assembly.GetManifestResourceStream(file.Resource);

        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The text in force: the reader's copy where there is a usable one, the
    /// shipped copy otherwise.
    ///
    /// A user file that no longer parses falls back rather than throwing. The
    /// application has to start; the editor is where a broken file gets reported,
    /// and it cannot be reached if loading it takes the window down.
    /// </summary>
    public string Current(ConfigFile file)
    {
        var path = PathFor(file);

        try
        {
            if (File.Exists(path))
            {
                var text = BomSafeJson.StripBom(File.ReadAllText(path));

                if (Validate(file, text).Accepted)
                {
                    return text;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable is the same as absent: the shipped copy answers.
        }

        return Shipped(file);
    }

    /// <summary>
    /// The bytes the engine actually loads, so a registry reads its override
    /// through exactly the path the editor writes.
    /// </summary>
    public byte[] CurrentBytes(ConfigFile file) => System.Text.Encoding.UTF8.GetBytes(Current(file));

    /// <summary>
    /// Exactly what is on disk, broken or not, or null when there is no copy.
    ///
    /// Deliberately not <see cref="Current"/>: that falls back to the shipped
    /// text so the application can start, which is right for loading and wrong
    /// for editing. Someone who has just broken the file in another editor needs
    /// to be shown the broken file, not quietly handed the original with their
    /// work gone.
    /// </summary>
    public string? ReadRaw(ConfigFile file)
    {
        var path = PathFor(file);

        try
        {
            return File.Exists(path) ? BomSafeJson.StripBom(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a candidate before it is allowed to become the file in force.
    ///
    /// Two things, in order: it has to be JSON at all, and it has to carry the
    /// array the engine reads. The second matters more than it looks -- a
    /// document that parses but has no "languages" would leave the picker empty
    /// with nothing anywhere saying why.
    /// </summary>
    public static ConfigVerdict Validate(ConfigFile file, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ConfigVerdict(false, "The file is empty.");
        }

        if (file.Format == ConfigFormat.Markdown)
        {
            // A glossary has no required content -- none at all is a legitimate
            // and common state. What it needs instead is a count, because a table
            // typed slightly wrong parses to zero rows and silently does nothing,
            // and "0 required terms" beside a file the reader just filled in is
            // the only thing that shows it.
            var tables = Slop.Glossary.Parse(text);

            return new ConfigVerdict(
                true,
                $"Valid. {tables.Terms.Count} required {(tables.Terms.Count == 1 ? "term" : "terms")}, "
                + $"{tables.Banned.Count} restricted {(tables.Banned.Count == 1 ? "word" : "words")}.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                BomSafeJson.StripBom(text),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            // The parser's own message names the line and position, which is the
            // only part of this that helps someone fix it.
            return new ConfigVerdict(false, ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ConfigVerdict(false, $"The top level must be an object carrying \"{file.RequiredArray}\".");
            }

            if (!document.RootElement.TryGetProperty(file.RequiredArray, out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return new ConfigVerdict(false, $"There is no \"{file.RequiredArray}\" array.");
            }

            var count = array.GetArrayLength();

            return count == 0
                ? new ConfigVerdict(false, $"\"{file.RequiredArray}\" is empty, so nothing would be loaded.")
                : new ConfigVerdict(true, $"Valid. {count} {(count == 1 ? "entry" : "entries")}.");
        }
    }

    /// <summary>
    /// Saves the reader's copy, or refuses and says why. Nothing is written
    /// unless it validates, so the file on disk is never one the engine cannot
    /// read.
    /// </summary>
    public ConfigVerdict Save(ConfigFile file, string? text)
    {
        var verdict = Validate(file, text);

        if (!verdict.Accepted)
        {
            return verdict;
        }

        try
        {
            Directory.CreateDirectory(folder);

            // No BOM, which is the other half of the rule the engine strips on
            // read: writing one here would hand the next reader the exact defect
            // BomSafeJson exists for.
            BomSafeJson.WriteAllText(PathFor(file), BomSafeJson.StripBom(text!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigVerdict(false, $"Could not be saved: {ex.Message}");
        }

        return verdict with { Detail = "Saved. " + verdict.Detail };
    }

    /// <summary>
    /// Puts the shipped copy back by deleting the reader's, rather than by
    /// writing the default over it. Deleting cannot half-succeed and cannot
    /// leave a file that is neither one thing nor the other.
    /// </summary>
    public ConfigVerdict Reset(ConfigFile file)
    {
        var path = PathFor(file);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return new ConfigVerdict(true, "Reset to the shipped file.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigVerdict(false, $"Could not be reset: {ex.Message}");
        }

        return new ConfigVerdict(true, "Already the shipped file.");
    }
}
