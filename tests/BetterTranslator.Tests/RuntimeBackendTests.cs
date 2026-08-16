using System.IO;
using System.Linq;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// The backend catalogue decides what the settings and installer surfaces may
/// offer, so its rules are asserted here rather than left to the views.
/// </summary>
public sealed class RuntimeBackendTests(ITestOutputHelper output)
{
    [Fact]
    public void TheProbeReportsThisMachine()
    {
        var probe = BackendCatalog.Probe();

        output.WriteLine($"vendor:      {probe.VendorLabel}");
        output.WriteLine($"NVIDIA:      {probe.HasNvidia}");
        output.WriteLine($"AMD:         {probe.HasAmd}");
        output.WriteLine($"Vulkan:      {probe.CanUseVulkan}");
        output.WriteLine($"CUDA:        {probe.CanUseCuda}");

        // CUDA needs NVIDIA's own driver library, so it can never be live
        // without an NVIDIA card present.
        if (probe.CanUseCuda)
        {
            probe.HasNvidia.Should().BeTrue("nvcuda.dll only ships with the NVIDIA driver");
        }
    }

    [Fact]
    public void CpuIsAlwaysOfferedAndTheGpuFlavoursFollowTheHardware()
    {
        var hardware = new HardwareReport(HasNvidia: false, HasAmd: true, CanUseVulkan: true, CanUseCuda: false);
        var options = new BackendCatalog(EmptyFolder()).Options(hardware);

        options.Single(o => o.Backend == RuntimeBackend.Cpu).IsSupported.Should().BeTrue();
        options.Single(o => o.Backend == RuntimeBackend.Vulkan).IsSupported.Should().BeTrue();
        options.Single(o => o.Backend == RuntimeBackend.Cuda).IsSupported
            .Should().BeFalse("an AMD card cannot run CUDA");
    }

    [Fact]
    public void SupportedButMissingReadsDifferentlyFromUnsupported()
    {
        var hardware = new HardwareReport(HasNvidia: true, HasAmd: false, CanUseVulkan: true, CanUseCuda: true);
        var options = new BackendCatalog(EmptyFolder()).Options(hardware);

        // Nothing is on disk in an empty folder, so CUDA is runnable here but
        // not yet downloaded -- a different sentence from "cannot run it".
        var cuda = options.Single(o => o.Backend == RuntimeBackend.Cuda);
        cuda.IsSupported.Should().BeTrue();
        cuda.IsInstalled.Should().BeFalse();
        cuda.CanSelect.Should().BeFalse();
        cuda.BlockedReason.Should().Be("Not downloaded yet");

        var onAmd = new BackendCatalog(EmptyFolder())
            .Options(new HardwareReport(false, true, true, false))
            .Single(o => o.Backend == RuntimeBackend.Cuda);
        onAmd.BlockedReason.Should().Be("This machine cannot run it");
    }

    [Fact]
    public void AStaleChoiceFallsBackToSomethingThatRuns()
    {
        var folder = FolderWith("BetterRuntimeCPU.dll");
        var catalog = new BackendCatalog(folder);
        var amd = new HardwareReport(HasNvidia: false, HasAmd: true, CanUseVulkan: true, CanUseCuda: false);

        // The card changed, or the flavour was removed. Either way the app must
        // still translate rather than refuse to start.
        catalog.Resolve(RuntimeBackend.Cuda, amd).Should().Be(RuntimeBackend.Cpu);
        catalog.Resolve(RuntimeBackend.Cpu, amd).Should().Be(RuntimeBackend.Cpu);
    }

    [Fact]
    public void TheRecommendationPrefersWhatIsBothRunnableAndPresent()
    {
        var folder = FolderWith("BetterRuntimeCPU.dll");
        var nvidia = new HardwareReport(HasNvidia: true, HasAmd: false, CanUseVulkan: true, CanUseCuda: true);

        // CUDA is the fastest this machine could run, but only CPU has shipped.
        new BackendCatalog(folder).Recommend(nvidia).Should().Be(RuntimeBackend.Cpu);

        // The CUDA DLL on its own is not installed, because it cannot load: it
        // imports cublas64_13.dll, which imports cublasLt64_13.dll, and neither
        // ships with the NVIDIA driver. Recommending it here is what sent a
        // machine to a runtime that silently fell back to the processor.
        var dllOnly = FolderWith("BetterRuntimeCPU.dll", "BetterRuntimeCUDA.dll");
        new BackendCatalog(dllOnly).IsInstalled(RuntimeBackend.Cuda).Should().BeFalse();
        new BackendCatalog(dllOnly).Recommend(nvidia).Should().Be(RuntimeBackend.Cpu);

        var withCuda = FolderWith(
            "BetterRuntimeCPU.dll", "BetterRuntimeCUDA.dll", "cublas64_13.dll", "cublasLt64_13.dll");

        new BackendCatalog(withCuda).IsInstalled(RuntimeBackend.Cuda).Should().BeTrue();
        new BackendCatalog(withCuda).Recommend(nvidia).Should().Be(RuntimeBackend.Cuda);
    }

    [Fact]
    public void ADependencyInAnotherFolderDoesNotCount()
    {
        // Beside it, not merely somewhere on the machine. NativeLibrary.Load
        // resolves a DLL's imports from that DLL's own directory -- confirmed by
        // loading the real artifact both ways -- so a copy in the models folder
        // does nothing for a runtime sitting next to the executable.
        var runtime = FolderWith("BetterRuntimeCUDA.dll");
        var libraries = FolderWith("cublas64_13.dll", "cublasLt64_13.dll");

        new BackendCatalog(runtime).IsInstalled(RuntimeBackend.Cuda).Should().BeFalse();
        new BackendCatalog(libraries).IsInstalled(RuntimeBackend.Cuda).Should().BeFalse();

        // Vulkan needs nothing carried beside it: vulkan-1.dll comes from the
        // driver. So the rule must not become "GPU flavours need companions".
        BackendCatalog.DependenciesFor(RuntimeBackend.Vulkan).Should().BeEmpty();
        BackendCatalog.DependenciesFor(RuntimeBackend.Cpu).Should().BeEmpty();
        BackendCatalog.DependenciesFor(RuntimeBackend.Cuda).Should().BeEquivalentTo(
            ["cublas64_13.dll", "cublasLt64_13.dll"]);
    }

    [Fact]
    public void ProjectorsAndBackupsAreNotModels()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bt-models-" + Guid.NewGuid().ToString("N"));
        var publisher = Path.Combine(folder, "somepublisher");
        Directory.CreateDirectory(publisher);

        File.WriteAllText(Path.Combine(publisher, "translategemma-4b-it.Q4_K_M.gguf"), "x");
        File.WriteAllText(Path.Combine(publisher, "translategemma-4b-it.mmproj-f16.gguf"), "x");
        File.WriteAllText(Path.Combine(publisher, "translategemma-4b-it.Q4_K_M.gguf.orig"), "x");
        File.WriteAllText(Path.Combine(publisher, "gemma3-1b-Q4_K_M.gguf"), "x");

        try
        {
            var library = new ModelLibrary();
            var found = library.Scan(folder);

            found.Select(m => m.Name).Should().BeEquivalentTo(
                ["translategemma-4b-it.Q4_K_M", "gemma3-1b-Q4_K_M"],
                "a vision projector is a companion file and .orig is a backup");

            found.Single(m => m.Name.StartsWith("translategemma", StringComparison.Ordinal))
                .Publisher.Should().Be("somepublisher");

            // A translation-tuned build wins the default, because a general chat
            // model answers the translate prompt with commentary.
            library.Default(found)!.Name.Should().StartWith("translategemma");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ScanningSomewhereThatIsNotThereIsEmptyRatherThanAThrow()
    {
        new ModelLibrary().Scan(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N")))
            .Should().BeEmpty();

        new ModelLibrary().Scan("").Should().BeEmpty();
    }

    private static string EmptyFolder() => FolderWith();

    private static string FolderWith(params string[] files)
    {
        var folder = Path.Combine(Path.GetTempPath(), "bt-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(folder, file), "x");
        }

        return folder;
    }
}
