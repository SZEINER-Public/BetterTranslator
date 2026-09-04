namespace BetterTranslator.Runtime.Models;

public enum ComponentKind
{
    /// <summary>The local inference runtime. Required, and never removable.</summary>
    Runtime,

    /// <summary>A translation model.</summary>
    Model,

    Verification,
}

/// <summary>
/// A file that must sit beside a component for it to work, and is not the
/// component itself.
///
/// This exists for one measured reason. BetterRuntimeCUDA.dll imports
/// cublas64_13.dll, which imports cublasLt64_13.dll, and neither ships with the
/// NVIDIA driver -- they are CUDA Toolkit redistributables. Shipping the one
/// file gave a DLL that could not load, and because the loader falls back
/// quietly, the only symptom was that translation ran at processor speed.
/// </summary>
public sealed record CompanionArtifact
{
    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256. Unlike the Drive artifacts these are real: the
    /// files were hashed from the CUDA Toolkit install they came from, so a
    /// completed download is checked against its contents and not only its
    /// length.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>Null until the artifact is hosted somewhere it can be fetched from.</summary>
    public Uri? DownloadUrl { get; init; }

    /// <summary>Why this file is needed, for a reader wondering what it is.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// One installable component. Sizes are the real byte counts the catalogue
/// declares, so every figure on screen comes from here through one formatter
/// rather than from a string typed into a view.
/// </summary>
public sealed record ModelComponent
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ComponentKind Kind { get; init; }

    /// <summary>One line under the name, for example "Runs the models on this machine".</summary>
    public required string Summary { get; init; }

    public required long SizeBytes { get; init; }

    public required string Version { get; init; }

    /// <summary>
    /// Where the artifact is fetched from. Null for the built-in catalogue
    /// entries until the owner supplies real URLs.
    /// </summary>
    public Uri? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public Downloads.ArtifactIntegrity? DeclaredIntegrity =>
        Sha256 is null
            ? null
            : new Downloads.ArtifactIntegrity { FileId = Id, FileName = FileName, SizeBytes = SizeBytes, Sha256 = Sha256 };

    /// <summary>
    /// Why this machine cannot use the component, or null when it can.
    ///
    /// Separate from <see cref="DownloadUrl"/> because they are separate facts
    /// and both surfaces need to tell them apart: "there is nothing to fetch"
    /// and "it can be fetched but it will not run here" are different sentences,
    /// and a row that collapsed them would either hide a flavour that exists or
    /// offer one that cannot start.
    ///
    /// Set by <see cref="ComponentCatalog.RuntimesFor"/> from the hardware
    /// probe, so one place decides and every surface reads the same verdict
    /// rather than each re-deriving it from the component id.
    /// </summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>Runnable here. False only where the probe said otherwise.</summary>
    public bool IsSupported => UnsupportedReason is null;

    /// <summary>
    /// Files that must land beside this one for it to load. Empty for every
    /// component except the CUDA runtime; see <see cref="CompanionArtifact"/>.
    /// </summary>
    public IReadOnlyList<CompanionArtifact> Companions { get; init; } = [];

    /// <summary>
    /// Everything that has to be fetched, which is what the button should price.
    ///
    /// Deliberately not <see cref="SizeBytes"/>, which stays the length of this
    /// component's own artifact because that is what the manifest verifies a
    /// completed download against. Conflating the two would fail the length
    /// check on every CUDA download.
    /// </summary>
    public long InstallBytes => SizeBytes + Companions.Sum(c => c.SizeBytes);

    /// <summary>
    /// Every artifact this needs has somewhere to come from. A component whose
    /// companion has no link cannot be installed in a working state, so offering
    /// it would promise exactly the broken install this field exists to prevent.
    /// </summary>
    public bool IsFetchable => DownloadUrl is not null && Companions.All(c => c.DownloadUrl is not null);

    /// <summary>
    /// Cannot be unticked or removed.
    ///
    /// No longer implied by being a runtime. There is a choice of runtime
    /// flavours now -- CPU, and a GPU build where the hardware can load one --
    /// and locking every one of them would mean a machine that can run Vulkan
    /// could never decline the CPU build. "At least one runtime" is enforced by
    /// the installer instead, which is the invariant that actually matters.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>Pre-selected on the first-run install list.</summary>
    public bool PreSelected { get; init; }

    /// <summary>True for a model the user pasted a link to.</summary>
    public bool IsCustom { get; init; }

    /// <summary>
    /// File name the artifact lands under inside the models folder.
    ///
    /// Composed from the id only where the source does not name the file. That
    /// scheme was the default until real artifacts arrived, and it was wrong for
    /// them: it would have written "translategemma.gguf" for a file the server
    /// calls "translategemma-4b-it.Q4_K_M.gguf", so the same weights downloaded
    /// through the app and fetched by any other tool would sit on disk under two
    /// names -- and the presence check, which matches on filename and length,
    /// would fetch a model that was already here.
    /// </summary>
    public string FileName =>
        ArtifactFileName ?? (Kind == ComponentKind.Runtime ? Id + ".zip" : Id + ".gguf");

    /// <summary>
    /// The name the server itself reports in Content-Disposition. Null for a
    /// component whose source has not been resolved.
    /// </summary>
    public string? ArtifactFileName { get; init; }
}
