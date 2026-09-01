using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using BetterTranslator.Mac.Seams;
using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.Mac.Platform;

public sealed class MacNativeRuntime(IAppPathsSeam paths) : INativeRuntimeSeam
{
    private const string LogicalName = "BetterRuntime";

    private static readonly string[] LogicalAliases = [LogicalName, "BetterRuntimeCPU"];

    private bool _installed;

    public string LibraryFileName => "libBetterRuntimeCPU.dylib";

    public IReadOnlyList<string> SearchPaths =>
    [
        paths.Runtime,
        paths.Models,
        AppContext.BaseDirectory,
    ];

    public string? ResolvedPath { get; private set; }

    public void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        foreach (var folder in SearchPaths)
        {
            BackendCatalog.SearchAlso(folder);
        }

        NativeLibrary.SetDllImportResolver(typeof(MacNativeRuntime).Assembly, Resolve);
        NativeLibrary.SetDllImportResolver(typeof(IAppPathsSeam).Assembly, Resolve);

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) => Resolve(name, null, null);
    }

    private IntPtr Resolve(string libraryName, Assembly? assembly, DllImportSearchPath? searchPath)
    {
        if (!LogicalAliases.Contains(libraryName, StringComparer.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var folder in SearchPaths)
        {
            var candidate = Path.Combine(folder, LibraryFileName);

            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                ResolvedPath = candidate;
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
