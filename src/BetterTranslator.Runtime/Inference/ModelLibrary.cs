namespace BetterTranslator.Runtime.Inference;

/// <summary>One GGUF found on disk.</summary>
public sealed record LocalModel(string Path, string Name, long SizeBytes)
{
    /// <summary>
    /// The folder immediately above the file, which for a model pulled from a
    /// hub is the publisher. Shown so two quantisations of the same model can
    /// be told apart.
    /// </summary>
    public string Publisher => new DirectoryInfo(System.IO.Path.GetDirectoryName(Path) ?? string.Empty).Name;
}

/// <summary>
/// Finds the models actually present, by walking the models folder for GGUFs,
/// rather than by listing what the catalogue says should be installed.
///
/// The catalogue route cannot work yet: its download URLs are unsupplied (D7)
/// and it expects one flat file per component id, while a real model folder is
/// nested by publisher and carries several quantisations of the same model.
/// Reading the disk means a folder populated by any other tool is usable today.
/// </summary>
public sealed class ModelLibrary
{
    /// <summary>
    /// Not every GGUF in a folder is something to translate with. Vision
    /// projectors are a companion file to a model, not a model; .orig is a
    /// backup left by whatever last rewrote the file.
    /// </summary>
    private static readonly string[] NotAModel = ["mmproj", ".orig"];

    /// <summary>
    /// Where models are looked for: the folder the app installs into, then every
    /// store another tool has populated. Reading those means a machine that
    /// already has models can translate without downloading them twice -- on the
    /// machine this was built against, 33 GiB of 47.
    ///
    /// The list is discovered rather than assumed. LM Studio's downloads folder
    /// is read from its settings, because someone who moved their models to
    /// another drive -- which is the usual reason to have fifteen-gigabyte files
    /// at all -- would otherwise have every one of them invisible here.
    /// </summary>
    public static IReadOnlyList<string> DefaultFolders(string appModelsFolder) =>
        [.. Engine.Models.ModelStores.Candidates(appModelsFolder).Select(s => s.Path)];

    public IReadOnlyList<LocalModel> Scan(params string[] folders)
    {
        var found = new Dictionary<string, LocalModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                if (NotAModel.Any(skip => path.Contains(skip, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var model = new LocalModel(path, Path.GetFileNameWithoutExtension(path), Length(path));

                // Nested folders can overlap, so the same file must not be
                // offered twice under two roots.
                if (model.SizeBytes > 0)
                {
                    found[path] = model;
                }
            }
        }

        return [.. found.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Finds a catalogue component among the files actually found, by the file
    /// name it ships under.
    ///
    /// Matched against a scan rather than built from the component's id: a
    /// model lands under whatever publisher folder its source uses, which need
    /// not resemble its id at all, and a composed path reports a model that is
    /// sitting on the disk as not installed.
    /// </summary>
    public LocalModel? Match(IReadOnlyList<LocalModel> models, string fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? null
            : models.FirstOrDefault(m => string.Equals(
                System.IO.Path.GetFileName(m.Path), fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Picks the model to fall back to when nothing is chosen yet. Prefers a
    /// translation-tuned build by name, because pointing a general chat model
    /// at the translate prompt produces commentary rather than a translation.
    /// </summary>
    public LocalModel? Default(IReadOnlyList<LocalModel> models) =>
        models.FirstOrDefault(m => m.Name.Contains("translate", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault();

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
