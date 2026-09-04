using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

/// <summary>Why the resolver decided a model is or is not already here.</summary>
public enum PresenceReason
{
    /// <summary>Found at the exact path the app installs to.</summary>
    InstalledHere,

    /// <summary>Found in a store another tool populated, so nothing needs fetching.</summary>
    InstalledElsewhere,

    /// <summary>A .part is beside the destination; a resume can continue from it.</summary>
    PartiallyDownloaded,

    /// <summary>Nothing on disk under that name.</summary>
    Missing,

    /// <summary>
    /// The name matches but the length does not, so the file on disk is a
    /// different build and must not be passed off as this one.
    /// </summary>
    WrongLength,

    /// <summary>
    /// The component's own artifact is here and something it needs beside it is
    /// not, so it cannot load. Its own state, because the alternatives both lie:
    /// installed would tick and lock a row that does not work, and missing would
    /// re-fetch gigabytes that are already on disk.
    /// </summary>
    MissingDependencies,

    MissingSystemRuntime,
}

/// <summary>
/// What the resolver found, and why. The reason reaches the UI: "already in your
/// LM Studio folder" and "not downloaded yet" are different sentences and a user
/// who is told the second when the first is true will go and wait for a download
/// that is not happening.
/// </summary>
public sealed record Presence
{
    public required PresenceReason Reason { get; init; }

    /// <summary>Where it was found. Null when it was not.</summary>
    public string? Path { get; init; }

    /// <summary>How much of a .part is already on disk, for a resume.</summary>
    public long BytesOnDisk { get; init; }

    public bool IsSatisfied => Reason is PresenceReason.InstalledHere or PresenceReason.InstalledElsewhere;

    /// <summary>Which required files are absent, named in the message.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>One line, in the house voice, naming what was actually decided.</summary>
    public string Explain(string name) => Reason switch
    {
        PresenceReason.InstalledHere => $"{name} is installed.",

        // Names the files, because "incomplete" leaves nothing to act on and
        // the whole failure this prevents was one nobody could see.
        PresenceReason.MissingDependencies =>
            $"{name} is here but cannot load without {string.Join(" and ", Missing)}, so the rest will be downloaded.",
        PresenceReason.MissingSystemRuntime =>
            $"{name} is installed, but {Inference.RuntimeSupport.VisualCppAdvice(Missing.Count > 0 ? Missing[0] : "msvcp140.dll")}",
        // Names the folder, because the whole point of the mark is that the file
        // exists but not where this install would put it.
        PresenceReason.InstalledElsewhere =>
            $"{name} is already downloaded, in {System.IO.Path.GetDirectoryName(Path)}. It is not in the folder chosen above.",
        PresenceReason.PartiallyDownloaded => $"{name} is part-downloaded; it will continue from {ByteSizeShort(BytesOnDisk)}.",
        PresenceReason.WrongLength => $"A file named like {name} is here but is a different build, so it will be downloaded.",
        _ => $"{name} is not downloaded yet.",
    };

    private static string ByteSizeShort(long bytes) => $"{bytes / 1024d / 1024d:F0} MB";
}

/// <summary>
/// Answers "is this already here" before any socket opens.
///
/// This is the largest speed feature in the download path and it is not about
/// transfer rate at all: on the machine this was built against, nine of the
/// eleven catalogue artifacts were already present in a store another tool had
/// populated -- 33.49 GiB of 47.09 GiB that a naive manager would have fetched
/// again. No amount of connection tuning competes with not downloading.
///
/// It reuses <see cref="ModelLibrary"/>'s scan rather than adding a second one,
/// because a presence check that disagrees with the model list is worse than no
/// presence check.
/// </summary>
public sealed class ModelResolver(InstallPaths paths, ModelLibrary library)
{
    /// <summary>
    /// Identity is the filename plus the byte length, never the catalogue id.
    /// ModelLibrary already records why an id cannot identify a model: a real
    /// store nests by publisher and carries several quantisations. Two of the
    /// eleven artifacts differ from a local file only by a hyphen in the name
    /// and by 16,224 bytes in length, so a looser match would conflate them.
    /// </summary>
    public Presence Resolve(ModelComponent component)
    {
        var destination = paths.PathFor(component);

        if (File.Exists(destination))
        {
            var found = Match(destination, component.SizeBytes, PresenceReason.InstalledHere);

            // Present is not the same as usable. A CUDA runtime whose cuBLAS
            // libraries are missing is a file that cannot load, and reporting it
            // installed ticks the row, locks it, and leaves no way to repair the
            // install from the screen that caused it.
            return found.Reason == PresenceReason.InstalledHere ? WithDependencies(component, found) : found;
        }

        // A runtime is loaded from beside the executable, not from the models
        // folder, so that is where "already installed" has to be asked. The CPU
        // flavour arrives there from the build, and offering to download a DLL
        // the app has already loaded would be absurd.
        if (component.Kind == ComponentKind.Runtime)
        {
            var beside = Path.Combine(AppContext.BaseDirectory, component.FileName);

            // Beside the executable is the runtime's home, so on the default
            // folder that is "here". Point the installer at a folder of your own
            // and it stops being: the question this screen asks is whether the
            // artifact is in the folder being installed to, and a copy that
            // lives somewhere else answers no however well it works.
            if (File.Exists(beside))
            {
                var found = Match(
                    beside,
                    component.SizeBytes,
                    paths.IsDefault ? PresenceReason.InstalledHere : PresenceReason.InstalledElsewhere);

                return found.Reason == PresenceReason.InstalledHere
                    ? WithDependencies(component, found)
                    : found;
            }
        }

        var partial = destination + ".part";
        if (File.Exists(partial))
        {
            var length = ComponentInstallState.BytesOf(partial);

            // A .part that has somehow reached or passed the full length is not
            // a resume point; it is a file whose promotion was interrupted, and
            // re-verifying it is cheaper and safer than guessing.
            if (length > 0 && length < component.SizeBytes)
            {
                return new Presence
                {
                    Reason = PresenceReason.PartiallyDownloaded,
                    Path = partial,
                    BytesOnDisk = length,
                };
            }
        }

        // Anywhere else on the machine, including the store another tool owns.
        var elsewhere = library
            .Scan([.. ModelLibrary.DefaultFolders(paths.ModelsFolder)])
            .FirstOrDefault(m => string.Equals(
                System.IO.Path.GetFileName(m.Path), component.FileName, StringComparison.OrdinalIgnoreCase));

        if (elsewhere is not null)
        {
            return Match(elsewhere.Path, component.SizeBytes, PresenceReason.InstalledElsewhere);
        }

        return new Presence { Reason = PresenceReason.Missing };
    }

    /// <summary>
    /// Downgrades a found component to <see cref="PresenceReason.MissingDependencies"/>
    /// when something it needs beside it is absent or is the wrong length.
    /// Beside it, not merely somewhere: the loader resolves a DLL's imports from
    /// that DLL's own folder, so the folder searched is the one the artifact was
    /// actually found in.
    /// </summary>
    private static Presence WithDependencies(ModelComponent component, Presence found)
    {
        var folder = Path.GetDirectoryName(found.Path);

        if (string.IsNullOrEmpty(folder))
        {
            return found;
        }

        var missing = ComponentInstallState.MissingIn(folder, component);

        if (missing.Count > 0)
        {
            return found with
            {
                Reason = PresenceReason.MissingDependencies,
                Missing = [.. missing.Select(m => m.FileName)],
            };
        }

        if (component.Kind != ComponentKind.Runtime)
        {
            return found;
        }

        var system = Inference.RuntimeSupport.MissingVisualCpp(folder);

        return system.Count == 0
            ? found
            : found with
            {
                Reason = PresenceReason.MissingSystemRuntime,
                Missing = system,
            };
    }

    /// <summary>
    /// A name match with the wrong length is reported as such rather than
    /// accepted. Passing off a different quantisation as the requested one would
    /// load silently and translate badly, which is far harder to diagnose than a
    /// download.
    /// </summary>
    private static Presence Match(string path, long expected, PresenceReason found)
    {
        var length = Math.Max(ComponentInstallState.BytesOf(path), 0);

        if (!ComponentInstallState.ArtifactMatches(path, expected))
        {
            return new Presence { Reason = PresenceReason.WrongLength, Path = path, BytesOnDisk = length };
        }

        return new Presence { Reason = found, Path = path, BytesOnDisk = length };
    }
}
