using System.Runtime.InteropServices;
using BetterTranslator.Core.Models;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// What the machine can actually run. Deliberately a capability probe rather
/// than a vendor lookup: the question is not "whose card is this" but "will
/// this backend load", and asking it directly means no registry read, no WMI
/// and no guessing from a marketing name.
/// </summary>
public sealed record HardwareReport(bool HasNvidia, bool HasAmd, bool CanUseVulkan, bool CanUseCuda)
{
    /// <summary>What to tell the user we found, in their words rather than ours.</summary>
    public string VendorLabel => (HasNvidia, HasAmd) switch
    {
        (true, true) => "NVIDIA and AMD graphics",
        (true, false) => "NVIDIA graphics",
        (false, true) => "AMD graphics",
        _ => "No discrete graphics detected",
    };
}

/// <summary>One backend as the settings and installer surfaces see it.</summary>
public sealed record BackendOption(
    RuntimeBackend Backend,
    string Title,
    string Summary,
    bool IsSupported,
    bool IsInstalled,
    bool IsRecommended)
{
    /// <summary>
    /// Selectable only when the hardware can run it and the DLL is on disk.
    /// Unsupported and not-yet-downloaded are different states and read
    /// differently, so the reason is spelled out rather than left to a
    /// greyed-out row.
    /// </summary>
    public bool CanSelect => IsSupported && IsInstalled;

    public string? BlockedReason => (IsSupported, IsInstalled) switch
    {
        (false, _) => "This machine cannot run it",
        (true, false) => "Not downloaded yet",
        _ => null,
    };
}

/// <summary>
/// Probes the machine, then reports which flavours it could run and which are
/// present. Both halves are needed: a machine may be able to run CUDA long
/// before the CUDA DLL has been built and shipped.
/// </summary>
public sealed class BackendCatalog(string? runtimeFolder = null)
{
    private readonly string? _folder = runtimeFolder;

    /// <summary>
    /// Everywhere a flavour may sit. Beside the executable is where the build
    /// puts it and where the loader looks first; the models folder is where a
    /// download lands. Both are searched, and the loader is given the same list,
    /// because a flavour that installs somewhere the loader cannot see would
    /// report itself available and then fail to start.
    /// </summary>
    public static IReadOnlyList<string> SearchPaths { get; private set; } = [AppContext.BaseDirectory];

    public static void SearchAlso(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !SearchPaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            SearchPaths = [.. SearchPaths, folder];
        }
    }

    private IEnumerable<string> Folders => _folder is null ? SearchPaths : [_folder];

    /// <summary>File name for a flavour, matching what the loader looks for.</summary>
    public static string FileNameFor(RuntimeBackend backend) => backend switch
    {
        RuntimeBackend.Cuda => "BetterRuntimeCUDA.dll",
        RuntimeBackend.Vulkan => "BetterRuntimeVulkan.dll",
        _ => "BetterRuntimeCPU.dll",
    };

    /// <summary>
    /// Files that must sit in the same folder as the flavour for it to load.
    ///
    /// Measured with dumpbin: the CUDA build imports cublas64_13.dll, which
    /// imports cublasLt64_13.dll, and neither ships with the NVIDIA driver.
    /// Vulkan needs only vulkan-1.dll, which every driver installs, so it needs
    /// nothing carried beside it.
    ///
    /// Same folder rather than anywhere on the machine, and that is not a
    /// simplification: NativeLibrary.Load resolves a DLL's dependencies from the
    /// directory of the DLL itself, which was confirmed by loading the real
    /// artifact both ways. A copy in some other folder does not satisfy this.
    /// </summary>
    public static IReadOnlyList<string> DependenciesFor(RuntimeBackend backend) => backend switch
    {
        RuntimeBackend.Cuda => ["cublas64_13.dll", "cublasLt64_13.dll"],
        _ => [],
    };

    /// <summary>
    /// Asks the loader, rather than reading a driver version out of the
    /// registry. nvcuda.dll ships with the NVIDIA driver and vulkan-1.dll is
    /// the Vulkan loader, so if either loads, that path is live.
    /// </summary>
    public static HardwareReport Probe()
    {
        var nvidia = CanLoad("nvapi64.dll") || CanLoad("nvcuda.dll");
        var amd = CanLoad("atiadlxx.dll") || CanLoad("amdhip64.dll");

        return new HardwareReport(
            HasNvidia: nvidia,
            HasAmd: amd,
            // A software rasteriser can answer the loader, so Vulkan is only
            // offered when there is also a card worth pointing it at.
            CanUseVulkan: CanLoad("vulkan-1.dll") && (nvidia || amd),
            CanUseCuda: CanLoad("nvcuda.dll"));
    }

    public IReadOnlyList<BackendOption> Options(HardwareReport? hardware = null)
    {
        var probe = hardware ?? Probe();
        var recommended = Recommend(probe);

        return
        [
            Build(RuntimeBackend.Cpu, "CPU", "Works on any machine. Best for short phrases.", true, recommended),
            Build(RuntimeBackend.Vulkan, "Vulkan", "GPU acceleration for AMD, Intel and NVIDIA.", probe.CanUseVulkan, recommended),
            Build(RuntimeBackend.Cuda, "CUDA", "NVIDIA only. Fastest where it runs.", probe.CanUseCuda, recommended),
        ];
    }

    /// <summary>
    /// The fastest flavour that is both runnable and present. Falls back to the
    /// fastest the hardware could run, so the recommendation still points
    /// somewhere useful before any GPU flavour has been built.
    /// </summary>
    public RuntimeBackend Recommend(HardwareReport? hardware = null)
    {
        var probe = hardware ?? Probe();
        var order = new[] { RuntimeBackend.Cuda, RuntimeBackend.Vulkan, RuntimeBackend.Cpu };

        var supported = order.Where(b => IsSupported(b, probe)).ToArray();

        return supported.FirstOrDefault(IsInstalled, supported.FirstOrDefault(RuntimeBackend.Cpu));
    }

    /// <summary>
    /// Installed means loadable, so the dependencies count and they have to be
    /// in the same folder as the flavour rather than merely present somewhere.
    ///
    /// Judging this by the main file alone is what let the settings screen offer
    /// CUDA on a machine where it could not load: the row was selectable, the
    /// choice was stored, the loader fell back to the CPU build, and the only
    /// symptom was that translation was slow.
    /// </summary>
    public bool IsInstalled(RuntimeBackend backend) =>
        Folders.Any(f => File.Exists(Path.Combine(f, FileNameFor(backend)))
                      && DependenciesFor(backend).All(d => File.Exists(Path.Combine(f, d))));

    /// <summary>
    /// Resolves what will actually be loaded. A stored choice can go stale --
    /// the card changes, or a flavour is removed -- so the caller is told both
    /// what it asked for and what it got.
    /// </summary>
    public RuntimeBackend Resolve(RuntimeBackend wanted, HardwareReport? hardware = null)
    {
        var probe = hardware ?? Probe();

        return IsSupported(wanted, probe) && IsInstalled(wanted) ? wanted : Recommend(probe);
    }

    private static bool IsSupported(RuntimeBackend backend, HardwareReport probe) => backend switch
    {
        RuntimeBackend.Cuda => probe.CanUseCuda,
        RuntimeBackend.Vulkan => probe.CanUseVulkan,
        _ => true,
    };

    private BackendOption Build(RuntimeBackend backend, string title, string summary, bool supported, RuntimeBackend recommended) =>
        new(backend, title, summary, supported, IsInstalled(backend), backend == recommended);

    private static bool CanLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
}
