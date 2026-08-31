using BetterTranslator.Core.Models;
using System.Runtime.InteropServices;

namespace BetterTranslator.Runtime.Inference;

public enum FlavorState
{
    NothingFound,
    Incomplete,
    DependencyMissing,
    Ready,
}

public sealed record FlavorFinding(
    FlavorState State,
    string? Flavor,
    string? Folder,
    string? Path,
    string? MissingModule,
    int HResult);

public static class RuntimeSupport
{
    public const int ErrorModNotFound = unchecked((int)0x8007007E);

    public const int ErrorBadExeFormat = unchecked((int)0x800700C1);

    private const string SupportFolderName = "runtime-support";

    public static IReadOnlyList<string> VisualCppModules { get; } =
        ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"];

    public static string CarriedFolder => System.IO.Path.Combine(AppContext.BaseDirectory, SupportFolderName);

    public static IReadOnlyList<string> RequiredBeside(RuntimeBackend backend) =>
        [.. VisualCppModules, .. BackendCatalog.DependenciesFor(backend)];

    public static IReadOnlyList<string> RequiredBeside(string flavorFileName) =>
        RequiredBeside(BackendOf(flavorFileName));

    public static RuntimeBackend BackendOf(string flavorFileName) =>
        flavorFileName.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ? RuntimeBackend.Cuda
        : flavorFileName.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ? RuntimeBackend.Vulkan
        : RuntimeBackend.Cpu;

    public static IReadOnlyList<string> MissingBeside(string folder, string flavorFileName) =>
        [.. RequiredBeside(flavorFileName).Where(module => !Present(folder, module) && !Present(CarriedFolder, module))];

    public static int CarryInto(string folder)
    {
        var source = CarriedFolder;

        if (!Directory.Exists(source) || !Directory.Exists(folder))
        {
            return 0;
        }

        var carried = 0;

        foreach (var module in VisualCppModules)
        {
            var from = System.IO.Path.Combine(source, module);
            var to = System.IO.Path.Combine(folder, module);

            if (!File.Exists(from) || Present(folder, module))
            {
                continue;
            }

            try
            {
                File.Copy(from, to, overwrite: false);
                carried++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return carried;
    }

    private static bool Present(string folder, string module)
    {
        try
        {
            return File.Exists(System.IO.Path.Combine(folder, module));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static (FlavorFinding Finding, IntPtr Handle) Open(string path, string flavor, string folder)
    {
        CarryInto(folder);

        var missing = MissingBeside(folder, System.IO.Path.GetFileName(path));

        if (missing.Count > 0)
        {
            return (new FlavorFinding(FlavorState.Incomplete, flavor, folder, path, missing[0], 0), IntPtr.Zero);
        }

        var handle = LoadScoped(path, out var hresult);

        return handle != IntPtr.Zero
            ? (new FlavorFinding(FlavorState.Ready, flavor, folder, path, null, 0), handle)
            : (new FlavorFinding(FlavorState.DependencyMissing, flavor, folder, path, null, hresult), IntPtr.Zero);
    }

    public static string Describe(FlavorFinding finding, IReadOnlyList<string> probed) => finding.State switch
    {
        FlavorState.Ready => string.Empty,

        FlavorState.NothingFound =>
            "No translation runtime is installed. Looked in "
            + string.Join(" and ", probed)
            + ". Open Models and runtimes and install the CPU runtime.",

        FlavorState.Incomplete =>
            finding.Flavor + " is installed in " + finding.Folder + " but the set is incomplete: "
            + finding.MissingModule
            + " is missing from that folder. Reinstall the runtime from Models and runtimes.",

        _ when finding.HResult == ErrorBadExeFormat =>
            finding.Flavor + " in " + finding.Folder + " is not a 64-bit library (0x"
            + Hex(finding.HResult)
            + "), so this process cannot load it. Reinstall the runtime from Models and runtimes.",

        _ =>
            finding.Flavor + " in " + finding.Folder + " was found and opened, and a module it depends on could not be loaded (0x"
            + Hex(finding.HResult)
            + "). Every module it needs has to sit in that same folder. Reinstall the runtime from Models and runtimes.",
    };

    private static string Hex(int hresult) =>
        hresult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string directory);

    private const uint SearchDllLoadDir = 0x00000100;

    private const uint SearchUserDirs = 0x00000400;

    private const uint SearchSystem32 = 0x00000800;

    public static IntPtr LoadScoped(string path, out int hresult)
    {
        var folder = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(folder))
        {
            AddDllDirectory(folder);
        }

        if (Directory.Exists(CarriedFolder))
        {
            AddDllDirectory(CarriedFolder);
        }

        var handle = LoadLibraryExW(path, IntPtr.Zero, SearchDllLoadDir | SearchUserDirs | SearchSystem32);

        hresult = handle == IntPtr.Zero
            ? Marshal.GetHRForLastWin32Error()
            : 0;

        return handle;
    }
}
