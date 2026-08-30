using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

/// <summary>
/// Where components land. The first-run installer may point the models folder
/// somewhere else; the database and the caches never move, so only this folder
/// is redirectable.
/// </summary>
public sealed class InstallPaths
{
    private readonly AppPaths _appPaths;
    private string? _override;

    public InstallPaths(AppPaths appPaths) => _appPaths = appPaths;

    /// <summary>The folder components are installed into.</summary>
    public string ModelsFolder => _override ?? _appPaths.ModelsFolder;

    /// <summary>The default, shown beside the Default folder option.</summary>
    public string DefaultModelsFolder => _appPaths.ModelsFolder;

    public bool IsDefault => _override is null;

    /// <summary>
    /// Points installs at a folder the user chose. Cancelling the dialog must
    /// not call this: the choice then stays on Default folder.
    /// </summary>
    public void UseFolder(string folder) => _override = folder;

    public void UseDefault() => _override = null;

    public string PathFor(ModelComponent component) => Path.Combine(ModelsFolder, component.FileName);

    public string PathFor(CompanionArtifact companion) => Path.Combine(ModelsFolder, companion.FileName);

    /// <summary>
    /// Installed means usable, so every companion counts. A CUDA runtime whose
    /// cuBLAS libraries are missing is on disk and cannot load, and reporting it
    /// installed is what let that ship as "CUDA is slow".
    ///
    /// Decided by <see cref="ComponentInstallState"/> rather than here, so every
    /// surface asks one implementation of the question.
    /// </summary>
    public bool IsInstalled(ModelComponent component) =>
        ComponentInstallState.IsInstalledIn(ModelsFolder, component);

    /// <summary>Which companions are still missing, for a message that names them.</summary>
    public IReadOnlyList<CompanionArtifact> MissingCompanions(ModelComponent component) =>
        ComponentInstallState.MissingIn(ModelsFolder, component);

    /// <summary>Real size on disk, or 0 when the component is not installed.</summary>
    public long InstalledBytes(ModelComponent component)
    {
        var total = Length(PathFor(component));

        foreach (var companion in component.Companions)
        {
            total += Length(PathFor(companion));
        }

        return total;
    }

    private static long Length(string path) => Math.Max(ComponentInstallState.BytesOf(path), 0);

    public void EnsureCreated() => Directory.CreateDirectory(ModelsFolder);

    /// <summary>
    /// Removes an installed component. The runtime is required and is never
    /// offered a Remove, so this refuses it rather than relying on the UI.
    /// </summary>
    public bool Remove(ModelComponent component)
    {
        if (component.IsRequired)
        {
            return false;
        }

        var path = PathFor(component);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);

        // The companions go with it. They are useless alone -- half a gigabyte
        // of cuBLAS with nothing that loads it -- and leaving them behind would
        // make a removed component still look partly installed on disk.
        foreach (var companion in component.Companions)
        {
            var beside = PathFor(companion);

            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }

        return true;
    }
}
