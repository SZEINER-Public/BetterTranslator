using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class FlavorFactAttribute : FactAttribute
{
    public const string Gate = "BETTERTRANSLATOR_FLAVOR_DLL";

    public FlavorFactAttribute()
    {
        if (Located() is null)
        {
            Skip = $"Set {Gate} to a BetterRuntime flavour to read its imports, or install one under the models folder.";
        }
    }

    public static string? Located()
    {
        if (Environment.GetEnvironmentVariable(Gate) is { Length: > 0 } named && File.Exists(named))
        {
            return named;
        }

        var models = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterTranslator",
            "models");

        if (!Directory.Exists(models))
        {
            return null;
        }

        return Directory.EnumerateFiles(models, "BetterRuntime*.dll").FirstOrDefault();
    }
}

public sealed class RuntimeFlavorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-flavor", Guid.NewGuid().ToString("N"));

    private readonly Func<string, bool> _systemProvides = RuntimeSupport.SystemProvides;

    public RuntimeFlavorTests()
    {
        Directory.CreateDirectory(_root);
        RuntimeSupport.SystemProvides = _ => false;
    }

    [Fact]
    public void AVisualCppModuleTheSystemProvidesIsNotReportedMissingAndTheAdviceNamesTheRedistributable()
    {
        var folder = Flavor("bare", []);

        RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll").Should().Equal(RuntimeSupport.VisualCppModules);

        RuntimeSupport.SystemProvides = _ => true;

        RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll").Should().BeEmpty("the redistributable installed on the machine satisfies the import");
        RuntimeSupport.MissingVisualCpp(folder).Should().BeEmpty();

        RuntimeSupport.SystemProvides = _ => false;

        var finding = new FlavorFinding(FlavorState.Incomplete, "BetterRuntimeCPU", folder, null, "msvcp140.dll", 0);
        var message = RuntimeSupport.Describe(finding, [folder]);

        message.Should().Contain(RuntimeSupport.VisualCppRedistributableUrl).And.Contain("msvcp140.dll").And.Contain(folder);
        message.Should().NotContain("Reinstall the runtime", "reinstalling cannot add a module the runtime archive never carried");
    }

    public void Dispose()
    {
        RuntimeSupport.SystemProvides = _systemProvides;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly string[] SystemModules =
    [
        "kernel32.dll",
        "advapi32.dll",
        "user32.dll",
        "ole32.dll",
        "oleaut32.dll",
        "shell32.dll",
        "bcrypt.dll",
        "ws2_32.dll",
        "ntdll.dll",
        "psapi.dll",
        "powrprof.dll",
        "vulkan-1.dll",
        "nvcuda.dll",
    ];

    private static bool IsSystem(string module) =>
        SystemModules.Contains(module, StringComparer.OrdinalIgnoreCase)
        || module.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)
        || module.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase);

    private string Flavor(string name, params string[] siblings)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "BetterRuntimeCPU.dll"), [0x4D, 0x5A]);

        foreach (var sibling in siblings)
        {
            File.WriteAllBytes(Path.Combine(folder, sibling), [0x4D, 0x5A]);
        }

        return folder;
    }

    [FlavorFact]
    public void TheShippedFlavourImportsNothingOutsideTheSystemSetAndWhatShipsBesideIt()
    {
        var path = FlavorFactAttribute.Located()!;
        var imports = PeImports.Of(path);

        imports.Should().NotBeEmpty("a native library with no import directory was not read correctly");

        var carried = RuntimeSupport.RequiredBeside(Path.GetFileName(path));

        var strangers = imports
            .Where(module => !IsSystem(module))
            .Where(module => !carried.Contains(module, StringComparer.OrdinalIgnoreCase))
            .ToList();

        strangers.Should().BeEmpty(
            "every module a flavour imports either ships with Windows or sits beside it in the flavour folder, and "
            + path + " imports " + string.Join(", ", imports));
    }

    [FlavorFact]
    public void TheVisualCppRuntimeIsWhatTheShippedFlavourNeedsBesideIt()
    {
        var path = FlavorFactAttribute.Located()!;
        var imports = PeImports.Of(path);

        var needed = imports
            .Where(module => !IsSystem(module))
            .Select(module => module.ToLowerInvariant())
            .ToList();

        needed.Should().NotBeEmpty("this is the set that has to be carried, and an empty one would prove nothing");

        needed.Should().BeSubsetOf(
            RuntimeSupport.RequiredBeside(Path.GetFileName(path)).Select(module => module.ToLowerInvariant()),
            "the manifest is what preflight checks, so it has to name every module the library actually imports");
    }

    [Fact]
    public void TheResolvedPathStaysInsideTheFlavourFolder()
    {
        var folder = Flavor("complete", [.. RuntimeSupport.VisualCppModules]);

        RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll").Should().BeEmpty();

        var (finding, handle) = RuntimeSupport.Open(
            Path.Combine(folder, "BetterRuntimeCPU.dll"),
            "BetterRuntimeCPU",
            folder);

        handle.Should().Be(IntPtr.Zero, "the stub is not a real library, so the load is expected to fail");

        finding.Path.Should().StartWith(folder, "a flavour is resolved by its own directory and never from elsewhere");
        finding.Folder.Should().Be(folder);
        finding.State.Should().Be(FlavorState.DependencyMissing, "the manifest passed, so what remains is the load");
    }

    [Fact]
    public void APartialExtractionFailsPreflightAndNamesTheFileThatIsMissing()
    {
        var complete = RuntimeSupport.VisualCppModules.ToArray();
        var removed = complete[1];
        var folder = Flavor("partial", [.. complete.Where(m => m != removed)]);

        var missing = RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll");

        missing.Should().ContainSingle().Which.Should().Be(removed);

        var (finding, handle) = RuntimeSupport.Open(
            Path.Combine(folder, "BetterRuntimeCPU.dll"),
            "BetterRuntimeCPU",
            folder);

        handle.Should().Be(IntPtr.Zero);
        finding.State.Should().Be(FlavorState.Incomplete);
        finding.MissingModule.Should().Be(removed);

        RuntimeSupport.Describe(finding, [folder]).Should().Contain(removed).And.Contain(folder);
    }

    [Fact]
    public void TheThreeFailureStatesReadAsThreeDifferentThings()
    {
        var folder = Flavor("states");

        var nothing = new FlavorFinding(FlavorState.NothingFound, null, null, null, null, 0);
        var incomplete = new FlavorFinding(FlavorState.Incomplete, "BetterRuntimeCPU", folder, null, "vcomp140.dll", 0);
        var dependency = new FlavorFinding(
            FlavorState.DependencyMissing,
            "BetterRuntimeCPU",
            folder,
            Path.Combine(folder, "BetterRuntimeCPU.dll"),
            null,
            RuntimeSupport.ErrorModNotFound);

        var probed = new[] { folder, Path.Combine(_root, "elsewhere") };

        var messages = new[]
        {
            RuntimeSupport.Describe(nothing, probed),
            RuntimeSupport.Describe(incomplete, probed),
            RuntimeSupport.Describe(dependency, probed),
        };

        messages.Should().OnlyHaveUniqueItems();
        messages.Should().AllSatisfy(message => message.Should().NotBeNullOrWhiteSpace());

        messages[0].Should().Contain(folder, "the state that found nothing still names where it looked");
        messages[0].Should().NotContain("could not be loaded", "nothing was found, so nothing failed to load");

        messages[1].Should().Contain("vcomp140.dll").And.Contain(folder);
        messages[1].Should().NotContain("No translation runtime is installed");

        messages[2].Should().Contain(folder).And.Contain("8007007E");
        messages[2].Should().NotContain("No translation runtime is installed",
            "a message must never say nothing was found while reporting a load failure for something that was");
    }

    [Fact]
    public void TheFailureIsToldApartByTheHresultRatherThanByItsWording()
    {
        var folder = Flavor("hresult");

        var missingModule = new FlavorFinding(
            FlavorState.DependencyMissing, "BetterRuntimeCPU", folder, null, null, RuntimeSupport.ErrorModNotFound);

        var wrongArchitecture = new FlavorFinding(
            FlavorState.DependencyMissing, "BetterRuntimeCPU", folder, null, null, RuntimeSupport.ErrorBadExeFormat);

        RuntimeSupport.Describe(missingModule, [folder])
            .Should().NotBe(RuntimeSupport.Describe(wrongArchitecture, [folder]));

        RuntimeSupport.Describe(wrongArchitecture, [folder]).Should().Contain("64-bit");
    }

    [Fact]
    public void EveryFlavourCarriesTheVisualCppRuntimeAndCudaStillCarriesItsOwn()
    {
        foreach (var backend in new[] { RuntimeBackend.Cpu, RuntimeBackend.Vulkan, RuntimeBackend.Cuda })
        {
            RuntimeSupport.RequiredBeside(backend)
                .Should().Contain(RuntimeSupport.VisualCppModules);
        }

        RuntimeSupport.RequiredBeside(RuntimeBackend.Cuda)
            .Should().Contain(BackendCatalog.DependenciesFor(RuntimeBackend.Cuda));
    }

    [Fact]
    public void TheCarriedRuntimeIsCopiedIntoAFlavourFolderThatIsMissingIt()
    {
        var source = Path.Combine(_root, "payload", "runtime-support");
        Directory.CreateDirectory(source);

        foreach (var module in RuntimeSupport.VisualCppModules)
        {
            File.WriteAllBytes(Path.Combine(source, module), [0x4D, 0x5A]);
        }

        var folder = Flavor("bare");

        RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll").Should().NotBeEmpty();

        foreach (var module in RuntimeSupport.VisualCppModules)
        {
            File.Copy(Path.Combine(source, module), Path.Combine(folder, module));
        }

        RuntimeSupport.MissingBeside(folder, "BetterRuntimeCPU.dll")
            .Should().BeEmpty("a flavour folder that carries the runtime is self-sufficient");
    }
}

internal static class PeImports
{
    public static IReadOnlyList<string> Of(string path)
    {
        var bytes = File.ReadAllBytes(path);

        var pe = BitConverter.ToInt32(bytes, 0x3C);
        var magic = BitConverter.ToUInt16(bytes, pe + 24);
        var sections = BitConverter.ToUInt16(bytes, pe + 6);
        var optionalSize = BitConverter.ToUInt16(bytes, pe + 20);

        var directory = magic == 0x20B ? pe + 24 + 112 : pe + 24 + 96;
        var importRva = BitConverter.ToUInt32(bytes, directory + 8);

        if (importRva == 0)
        {
            return [];
        }

        var table = new List<(uint Va, uint Size, uint Raw)>();

        for (var i = 0; i < sections; i++)
        {
            var header = pe + 24 + optionalSize + (i * 40);

            table.Add((
                BitConverter.ToUInt32(bytes, header + 12),
                BitConverter.ToUInt32(bytes, header + 8),
                BitConverter.ToUInt32(bytes, header + 20)));
        }

        int Offset(uint rva)
        {
            foreach (var (va, size, raw) in table)
            {
                if (rva >= va && rva < va + Math.Max(size, 1))
                {
                    return (int)(raw + (rva - va));
                }
            }

            return 0;
        }

        string Read(int at)
        {
            var end = at;

            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, at, end - at);
        }

        var names = new List<string>();

        for (var entry = Offset(importRva); entry > 0; entry += 20)
        {
            var nameRva = BitConverter.ToUInt32(bytes, entry + 12);

            if (nameRva == 0)
            {
                break;
            }

            names.Add(Read(Offset(nameRva)));
        }

        return names;
    }
}
