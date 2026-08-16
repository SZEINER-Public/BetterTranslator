using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.Runtime.Models;

/// <summary>
/// The components the application offers.
///
/// D7 is resolved, and now for every row rather than only the models: each
/// carries a real download URL, and its size and file name are what the server
/// reports rather than what the design estimated. All three runtime flavours
/// have been built and measured. See B19, B30 and B46.
/// </summary>
public sealed class ComponentCatalog
{
    private readonly List<ModelComponent> _custom = [];

    public static IReadOnlyList<ModelComponent> BuiltIn { get; } =
    [
        // Sizes and file names below are measured from the artifact source, not
        // declared: each is the Content-Range total and Content-Disposition name
        // the server returned on 2026-08-02. The earlier figures were estimates
        // and both were wrong -- EuroLLM Q4_K_M is 5.20 GiB, not 1.7 GB.
        new()
        {
            Id = "eurollm",
            Name = "EuroLLM",
            Kind = ComponentKind.Model,
            Summary = "Quick, wide European coverage - pairs with Fast",
            SizeBytes = 5582838208,
            Version = "9B-Q4_K_M",
            ArtifactFileName = "EuroLLM-9B-Instruct-Q4_K_M.gguf",
            DownloadUrl = ModelUrlParser.DriveDownloadUrl("1WXDoH1U4ermIJI9oTxnzjNyZLzLh4RlK"),
            PreSelected = true,
        },
        new()
        {
            Id = "translategemma",
            Name = "TranslateGemma",
            Kind = ComponentKind.Model,
            Summary = "Translation-tuned, slower - pairs with Thinking",

            // The share link supplied on 2026-08-02 points at the hyphen build,
            // which is a different artifact from the dot build this row carried
            // before: 2,489,909,312 bytes against 2,489,893,088, the 16,224-byte
            // difference recorded in B19. Both ids are live and both are in the
            // manifest, so the size and name here follow the link rather than the
            // other way round -- carrying the old figures against the new id
            // would fail the length check on every download.
            SizeBytes = 2489909312,
            Version = "4b-it-Q4_K_M",
            ArtifactFileName = "translategemma-4b-it-Q4_K_M.gguf",
            DownloadUrl = ModelUrlParser.DriveDownloadUrl("15Yt13K9DhtwnHvTHFqh4LYKq6on_KQvr"),
            PreSelected = true,
        },
    ];

    /// <summary>Built-ins plus anything added from a pasted link.</summary>
    public IReadOnlyList<ModelComponent> All => [.. BuiltIn, .. _custom];

    public IReadOnlyList<ModelComponent> Custom => _custom;

    /// <summary>
    /// Adds a model from a pasted link. Returns the rejection when the link is
    /// not one this application can fetch.
    /// </summary>
    public ModelUrlResult AddCustom(string? link, long assumedSizeBytes = 0)
    {
        var parsed = ModelUrlParser.Parse(link);

        if (!parsed.IsAccepted)
        {
            return parsed;
        }

        if (_custom.Any(c => c.DownloadUrl == parsed.DownloadUrl))
        {
            return ModelUrlResult.Reject($"{parsed.ModelName} is already in the list.");
        }

        _custom.Add(new ModelComponent
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
            Name = parsed.ModelName!,
            Kind = ComponentKind.Model,
            Summary = "added by you",
            SizeBytes = assumedSizeBytes,
            Version = "custom",
            DownloadUrl = parsed.DownloadUrl,
            IsCustom = true,
        });

        return parsed;
    }

    public bool RemoveCustom(string id) => _custom.RemoveAll(c => c.Id == id) > 0;

    /// <summary>
    /// Every runtime flavour, with the ones this machine cannot load marked
    /// rather than dropped.
    ///
    /// One row per backend rather than one BetterRuntime row, because the choice
    /// is a different download, not a setting: each flavour is its own
    /// self-contained DLL. CPU is always the floor that runs anywhere.
    ///
    /// This used to filter on the probe, so a Radeon was never shown a CUDA row
    /// at all. That left the two surfaces disagreeing about the same question:
    /// <see cref="BackendCatalog.Options"/> has always returned all three and
    /// marked the unrunnable one "This machine cannot run it", so the settings
    /// screen showed a CUDA card while the installer silently omitted it. The
    /// installer follows the settings screen now. The reason the filter existed
    /// -- that a row offering CUDA on a Radeon invites a download that will never
    /// start -- is answered by locking the row instead of hiding it, which says
    /// the flavour exists and why it is unavailable without letting anyone spend
    /// 138 MB on it.
    ///
    /// Several may be ticked. Someone who wants the GPU build for speed and the
    /// CPU build as a fallback is asking for a reasonable thing.
    /// </summary>
    public static IReadOnlyList<ModelComponent> RuntimesFor(HardwareReport hardware) =>
    [
        // 3,595,264 bytes is measured from the artifact, not estimated. The
        // 42 MB this once claimed was a guess made before one was built.
        Runtime(
            RuntimeBackend.Cpu,
            "BetterRuntime (CPU)",
            "Runs the models on the processor. Works on any machine.",
            3595264,
            ModelUrlParser.DriveDownloadUrl("12SVa7db8qTjb7daBYGbWulH3EteCeqq1"),
            preSelected: true),

        // 54,225,408 bytes is measured, and is well over the 25-40 MB the
        // design estimated: SPIR-V shaders are embedded once for every
        // supported operation, not per card.
        Runtime(
            RuntimeBackend.Vulkan,
            "BetterRuntime (AMD Vulkan)",
            "Runs the models on the graphics card. Much faster than the processor.",
            54225408,
            ModelUrlParser.DriveDownloadUrl("12WQ--DVStQb3gH3FcehpnkF7tfTe5eDM"),
            unsupportedReason: hardware.CanUseVulkan ? null : NoVulkan),

        // 145,155,584 bytes, measured from the link on 2026-08-08. The 560 MB
        // this row carried was an estimate made before a CUDA build existed,
        // and being four times the real figure it would have failed the length
        // check on every completed download.
        Runtime(
            RuntimeBackend.Cuda,
            "BetterRuntime (NVIDIA CUDA)",
            "Runs the models on an NVIDIA GPU. Fastest where it runs.",
            145155584,
            ModelUrlParser.DriveDownloadUrl("1ipcWlhbPl5JCWI8Lt1Krxt6zclpdxRGp"),
            unsupportedReason: hardware.CanUseCuda ? null : NoCuda,
            companions: CudaLibraries),
    ];

    /// <summary>
    /// What BetterRuntimeCUDA.dll imports and cannot run without.
    ///
    /// Measured with dumpbin against the shipped DLL, not assumed: it imports
    /// cublas64_13.dll, which in turn imports cublasLt64_13.dll. Neither is part
    /// of the NVIDIA display driver -- both are CUDA Toolkit redistributables --
    /// so a machine with an NVIDIA card and no toolkit has an NVIDIA driver, a
    /// CUDA-capable GPU, and a runtime that will not load.
    ///
    /// The Vulkan flavour needs no such thing: it imports only vulkan-1.dll,
    /// which every GPU driver installs. That asymmetry is the whole reason CUDA
    /// measured slower than Vulkan on the same card.
    ///
    /// Lengths and hashes are measured from CUDA Toolkit 13.3. The hashes are
    /// real -- these files were read, not merely sized -- which makes them the
    /// only artifacts here whose contents are verified rather than just their
    /// length.
    /// </summary>
    private static IReadOnlyList<CompanionArtifact> CudaLibraries =>
    [
        new()
        {
            FileName = "cublas64_13.dll",
            SizeBytes = 52697712,
            Sha256 = "8c6bac24474af29627ec96500025a5d410a1b7e8ffa00e1fe13b1781fc6b3ddc",
            Reason = "NVIDIA cuBLAS, which the CUDA runtime calls for matrix multiplication.",
            DownloadUrl = ModelUrlParser.DriveDownloadUrl("10cqIlFhUsJkKQqr1WI_OFOsVZw4R6nKD"),
        },
        new()
        {
            FileName = "cublasLt64_13.dll",
            SizeBytes = 463655536,
            Sha256 = "e323abd89e0f03c1db91a09d7aeb1928de08e1f86192cc713b335fefe3e368a9",
            Reason = "NVIDIA cuBLASLt, which cublas64_13.dll itself imports.",
            DownloadUrl = ModelUrlParser.DriveDownloadUrl("1fyANf9foXLVMHcA5VQON2_YYYFLNSjVJ"),
        },
    ];

    /// <summary>
    /// Why a flavour is unavailable, in the same words
    /// <see cref="BackendOption.BlockedReason"/> uses, so the installer and the
    /// settings card do not say the same thing differently. Each names the
    /// missing half rather than saying "unsupported".
    /// </summary>
    private const string NoCuda = "This machine cannot run it - no NVIDIA graphics detected.";

    private const string NoVulkan = "This machine cannot run it - no Vulkan-capable graphics detected.";

    /// <summary>
    /// All three flavours have been built and measured, so a link is required
    /// rather than optional. It was optional while only CPU existed, and a
    /// defaulted null is exactly how a fourth flavour would be added link-less
    /// by accident -- offered, ticked, and then failing with "No download link
    /// is configured for this component".
    /// </summary>
    private static ModelComponent Runtime(
        RuntimeBackend backend,
        string name,
        string summary,
        long size,
        Uri url,
        bool preSelected = false,
        string? unsupportedReason = null,
        IReadOnlyList<CompanionArtifact>? companions = null) => new()
    {
        Id = "betterruntime-" + backend.ToString().ToLowerInvariant(),
        Name = name,
        Kind = ComponentKind.Runtime,
        Summary = summary,
        SizeBytes = size,
        UnsupportedReason = unsupportedReason,
        Companions = companions ?? [],

        // Asked of the loader rather than composed here. This used to build
        // "BetterRuntime" + flavour.ToUpperInvariant() + ".dll", which spells the
        // Vulkan artifact BetterRuntimeVULKAN.dll while the loader and the
        // manifest both call it BetterRuntimeVulkan.dll -- so a download landed
        // under a name that only matched because Windows paths are
        // case-insensitive. One source of truth removes the coincidence.
        ArtifactFileName = BackendCatalog.FileNameFor(backend),
        Version = "b4120",
        DownloadUrl = url,
        PreSelected = preSelected,
    };

    /// <summary>
    /// Total of a selection, the figure the action button prices.
    ///
    /// InstallBytes, not SizeBytes: a component's own artifact is not always
    /// everything that has to be fetched for it, and pricing CUDA at 138 MB when
    /// installing it moves 631 MB would understate it fourfold.
    /// </summary>
    public static long TotalBytes(IEnumerable<ModelComponent> selection) =>
        selection.Sum(c => c.InstallBytes);
}
