using System.Linq;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What the two offering surfaces do with a flavour this machine cannot run.
///
/// Both now list every flavour: the installer used to filter on the hardware
/// probe while the settings screen never did, so the same question had two
/// answers. Listing it is only safe because it cannot be ticked, and that is
/// what these assert -- the row exists, says why it is unavailable, refuses
/// selection, and adds nothing to the figure the Install button prices.
/// </summary>
public sealed class RuntimeFlavourOfferTests
{
    private static readonly HardwareReport NoGpu = new(false, false, false, false);

    private static readonly HardwareReport Nvidia =
        new(HasNvidia: true, HasAmd: false, CanUseVulkan: false, CanUseCuda: true);

    private static ModelComponent Flavour(HardwareReport hardware, RuntimeBackend backend) =>
        ComponentCatalog
            .RuntimesFor(hardware)
            .Single(c => c.Id == "betterruntime-" + backend.ToString().ToLowerInvariant());

    [Fact]
    public void ABlockedRowCannotBeTickedHoweverItIsAskedTo()
    {
        var item = new InstallItemViewModel(Flavour(NoGpu, RuntimeBackend.Cuda));

        item.IsBlocked.Should().BeTrue();
        item.BlockedReason.Should().Contain("cannot run it");
        item.HasBlockedReason.Should().BeTrue();
        item.CanDeselect.Should().BeFalse("the card and its indicator both bind to this");

        // Set directly rather than through the view: IsEnabled on the template
        // stops the pointer, and the rule has to hold for anything else that
        // writes the property -- a restored selection, a test, a future caller.
        item.IsSelected = true;
        item.IsSelected.Should().BeFalse("a flavour that cannot run here must never end up selected");
    }

    [Fact]
    public void ARunnableRowIsUnaffectedByTheSameRule()
    {
        var cpu = new InstallItemViewModel(Flavour(NoGpu, RuntimeBackend.Cpu));

        cpu.IsBlocked.Should().BeFalse();
        cpu.BlockedReason.Should().BeNull();
        cpu.CanDeselect.Should().BeTrue();

        cpu.IsSelected = true;
        cpu.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void BlockedRowsAddNothingToWhatTheInstallButtonPrices()
    {
        var rows = ComponentCatalog
            .RuntimesFor(NoGpu)
            .Select(c => new InstallItemViewModel(c))
            .ToList();

        // Tick everything, the way a reader pressing every card would.
        foreach (var row in rows)
        {
            row.IsSelected = true;
        }

        var selected = rows.Where(r => r.IsSelected).ToList();

        selected.Select(r => r.Component.Id).Should().BeEquivalentTo(
            ["betterruntime-cpu"],
            "only the flavour that runs here survives being ticked");

        // The figure the button shows is the sum over selected rows, so the two
        // GPU flavours contribute nothing rather than 192 MB of unusable DLL.
        ComponentCatalog.TotalBytes(selected.Select(r => r.Component)).Should().Be(3595264);
    }

    [Fact]
    public void TheGetButtonAppearsOnlyWhereThereIsSomethingToGet()
    {
        var both = new HardwareReport(HasNvidia: true, HasAmd: false, CanUseVulkan: true, CanUseCuda: true);
        var options = new BackendCatalog(EmptyFolder()).Options(both);

        BackendOptionViewModel Card(RuntimeBackend backend) => new(
            options.Single(o => o.Backend == backend),
            Flavour(both, backend),
            _ => false);

        // Runnable, absent, and every artifact it needs has a link: the one
        // state where "Not downloaded yet" is an instruction rather than a dead
        // end. Priced through the one byte formatter rather than a literal, so
        // the assertion does not depend on the machine's decimal separator.
        var vulkan = Card(RuntimeBackend.Vulkan);
        vulkan.CanFetch.Should().BeTrue();
        vulkan.BlockedReason.Should().Be("Not downloaded yet");
        vulkan.FetchLabel.Should().Be("Get " + ByteSize.Format(54225408));

        // CUDA is fetchable now that its cuBLAS libraries are hosted, and it is
        // priced at everything the press will move -- 631 MB, not the 138 MB of
        // its own DLL. Understating it fourfold is its own defect.
        var cuda = Card(RuntimeBackend.Cuda);
        cuda.CanFetch.Should().BeTrue();
        cuda.FetchLabel.Should().Be("Get " + ByteSize.Format(661508832));
    }

    /// <summary>
    /// The rule that kept the broken CUDA install off the screen, tested against
    /// a component built for the purpose rather than against the catalogue.
    ///
    /// It had a live example for exactly as long as the cuBLAS libraries were
    /// unhosted. Pinning it to that state would have deleted the rule the moment
    /// the links arrived, which is the moment it stops being observable and
    /// starts being only a guarantee.
    /// </summary>
    [Fact]
    public void AComponentWhoseCompanionHasNoLinkCannotBeInstalled()
    {
        var withoutLink = new ModelComponent
        {
            Id = "test-runtime",
            Name = "A runtime",
            Kind = ComponentKind.Runtime,
            Summary = "test",
            SizeBytes = 100,
            Version = "1",
            DownloadUrl = new Uri("https://example.invalid/runtime.dll"),
            Companions =
            [
                new() { FileName = "needed.dll", SizeBytes = 900, Reason = "test", DownloadUrl = null },
            ],
        };

        withoutLink.IsFetchable.Should().BeFalse(
            "fetching it would land a runtime with nothing beside it that it can load against");

        // The pricing is the sum either way, so a half-installable component
        // cannot quietly advertise the cheaper half.
        withoutLink.InstallBytes.Should().Be(1000);

        var withLink = withoutLink with
        {
            Companions = [withoutLink.Companions[0] with { DownloadUrl = new Uri("https://example.invalid/needed.dll") }],
        };

        withLink.IsFetchable.Should().BeTrue();
    }

    [Fact]
    public void TheCudaRowPricesItsLibrariesRatherThanJustItsOwnDll()
    {
        var cuda = Flavour(Nvidia, RuntimeBackend.Cuda);

        cuda.Companions.Select(c => c.FileName).Should().BeEquivalentTo(
            ["cublas64_13.dll", "cublasLt64_13.dll"],
            "measured with dumpbin against the shipped DLL, not assumed");

        // SizeBytes stays the DLL's own length, because that is what the
        // manifest verifies a completed download against. Conflating the two
        // would fail the length check on every CUDA download.
        cuda.SizeBytes.Should().Be(145155584);
        cuda.InstallBytes.Should().Be(145155584 + 52697712 + 463655536);

        // Real hashes, unlike every Drive artifact: these were read off the
        // toolkit rather than sized over the wire.
        cuda.Companions.Should().AllSatisfy(c => c.Sha256.Should().NotBeNullOrWhiteSpace());

        // Neither GPU flavour that ships today needs anything beside it except
        // this one, so the rule is not "runtimes have companions".
        Flavour(Nvidia, RuntimeBackend.Cpu).Companions.Should().BeEmpty();
        Flavour(Nvidia, RuntimeBackend.Vulkan).Companions.Should().BeEmpty(
            "the Vulkan build imports only vulkan-1.dll, which every GPU driver installs");
    }

    [Fact]
    public void AnInstalledFlavourOffersNothingToFetch()
    {
        var complete = FolderWith("BetterRuntimeCUDA.dll", "cublas64_13.dll", "cublasLt64_13.dll");
        var option = new BackendCatalog(complete).Options(Nvidia).Single(o => o.Backend == RuntimeBackend.Cuda);

        new BackendOptionViewModel(option, Flavour(Nvidia, RuntimeBackend.Cuda), _ => false)
            .CanFetch.Should().BeFalse("everything it needs is already on disk");
    }

    [Fact]
    public void AFlavourMissingItsLibrariesIsStillOfferedSoTheInstallCanBeCompleted()
    {
        // The DLL is here and cannot load. Offering the download is the repair
        // route, and it is the state the reported defect actually shipped in.
        var partial = FolderWith("BetterRuntimeCUDA.dll");
        var option = new BackendCatalog(partial).Options(Nvidia).Single(o => o.Backend == RuntimeBackend.Cuda);

        option.IsInstalled.Should().BeFalse("its cuBLAS libraries are not beside it, so it cannot load");
        option.CanSelect.Should().BeFalse("selecting it would store a choice that falls back to the processor");
        option.BlockedReason.Should().Be("Not downloaded yet");

        new BackendOptionViewModel(option, Flavour(Nvidia, RuntimeBackend.Cuda), _ => false)
            .CanFetch.Should().BeTrue();
    }

    /// <summary>
    /// Unsupported outranks fetchable. CUDA on a Radeon has a real link and a
    /// real size, so the older two-state ladder would have called it
    /// "Available" on a machine that cannot load it.
    /// </summary>
    [Theory]
    [InlineData(true, true, true, "Installed")]
    [InlineData(false, true, true, "Available")]
    [InlineData(false, true, false, "Not built yet")]
    [InlineData(false, false, true, "Cannot run here")]
    [InlineData(true, false, true, "Installed")]
    public void TheDownloadsRowNamesTheStateItIsActuallyIn(
        bool installed, bool supported, bool canFetch, string expected) =>
        new CatalogueRow("BetterRuntime (NVIDIA CUDA)", "Runtime", "138,4 MB", installed, canFetch, supported)
            .StateLabel.Should().Be(expected);

    /// <summary>
    /// The reported defect, asserted at the screen that showed it: the installer
    /// listed two runtime rows on an AMD machine and CUDA was nowhere, so it
    /// read as missing rather than as unavailable.
    ///
    /// Written against the real probe rather than a fixed report, because the
    /// filtering this replaced lived between the probe and the list -- a test
    /// that supplied its own hardware would have passed against the old code
    /// too. The invariant holds on any machine: the installer lists exactly the
    /// flavours the catalogue offers for what was probed, and marks them the
    /// same way.
    /// </summary>
    [Fact]
    public void TheInstallerListsEveryFlavourTheCatalogueOffers()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bt-first-" + Guid.NewGuid().ToString("N"));
        var paths = new BetterTranslator.Runtime.Downloads.InstallPaths(new AppPaths(root));
        paths.EnsureCreated();

        try
        {
            using var http = new System.Net.Http.HttpClient();
            var installer = new FirstRunViewModel(paths, http);

            var expected = ComponentCatalog.RuntimesFor(BackendCatalog.Probe());
            var listed = installer.Items.Where(i => i.Component.Kind == ComponentKind.Runtime).ToList();

            listed.Select(i => i.Component.Id).Should().BeEquivalentTo(
                expected.Select(c => c.Id),
                "a flavour that cannot run here is shown and locked, never dropped from the list");

            listed.Should().HaveCount(3, "there are three flavours and all three have been built");

            // Every listed row agrees with the catalogue about whether it can be
            // used, and an unusable one carries its reason to the view. The XAML
            // binds these two names, so a rename that silently broke the row
            // would fail here rather than render a blank line.
            foreach (var row in listed)
            {
                var offered = expected.Single(c => c.Id == row.Component.Id);

                row.IsBlocked.Should().Be(!offered.IsSupported);
                row.HasBlockedReason.Should().Be(!offered.IsSupported);

                if (row.IsBlocked)
                {
                    row.BlockedReason.Should().NotBeNullOrWhiteSpace();
                    row.IsSelected.Should().BeFalse("the installer must not open with an unusable row ticked");
                }
            }
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (System.IO.IOException)
            {
            }
        }
    }

    /// <summary>
    /// The defect that made the CUDA slowdown invisible, asserted at the panel
    /// that hid it.
    ///
    /// A CUDA build with no cuBLAS beside it cannot load; the loader falls back
    /// to the CPU build; and this panel went on reporting "NVIDIA CUDA" because
    /// it read the chosen backend rather than the loaded one. The only remaining
    /// symptom was that generation was slow, which reads as a slow machine.
    ///
    /// Static state, so it is set and put back rather than left for whatever
    /// test runs next.
    /// </summary>
    [Fact]
    public void ThePanelReportsTheFlavourThatLoadedNotTheOneThatWasChosen()
    {
        var before = LocalTranslator.LoadedFlavor;
        var beforeNote = LocalTranslator.FlavorNote;

        using var translator = new LocalTranslator();
        var status = new RuntimeStatusViewModel(translator, () => string.Empty, () => RuntimeBackend.Cuda);

        try
        {
            // Nothing loaded yet: the choice is the honest answer, because it is
            // what will be tried and saying nothing would be worse.
            LocalTranslator.RecordLoaded(null, null);
            status.BackendLabel.Should().Be("NVIDIA CUDA");
            status.HasBackendNote.Should().BeFalse();

            // CUDA was chosen; the CPU build is what actually loaded.
            LocalTranslator.RecordLoaded(
                "BetterRuntimeCPU",
                "BetterRuntimeCUDA.dll is installed but could not be loaded, so BetterRuntimeCPU.dll is running instead.");

            status.BackendLabel.Should().Be("CPU", "the panel must name the library that is running");
            status.HasBackendNote.Should().BeTrue();
            status.BackendNote.Should().Contain("could not be loaded");
        }
        finally
        {
            LocalTranslator.RecordLoaded(before, beforeNote);
        }
    }

    /// <summary>
    /// The placement contract, which is the whole reason the fix works.
    ///
    /// Measured, not reasoned about: loading the real BetterRuntimeCUDA.dll with
    /// nothing beside it raises DllNotFoundException 0x8007007E, and loading the
    /// same file with both cuBLAS libraries in the same folder succeeds and
    /// resolves br_init -- on a machine with no NVIDIA hardware at all, because
    /// the imports are static and nvcuda is not one of them. Windows resolves
    /// those imports from the folder of the DLL being loaded, transitively:
    /// cublasLt is imported by cuBLAS rather than by the runtime, and it is
    /// found too.
    ///
    /// So a download that put the companions anywhere else would install three
    /// correct files and still not load. This holds the install paths together.
    /// </summary>
    [Fact]
    public void AComponentAndItsCompanionsInstallIntoTheSameFolder()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bt-place-" + Guid.NewGuid().ToString("N"));
        var paths = new BetterTranslator.Runtime.Downloads.InstallPaths(new AppPaths(root));

        try
        {
            var cuda = Flavour(Nvidia, RuntimeBackend.Cuda);
            var runtimeFolder = System.IO.Path.GetDirectoryName(paths.PathFor(cuda));

            cuda.Companions.Should().NotBeEmpty();

            foreach (var companion in cuda.Companions)
            {
                System.IO.Path.GetDirectoryName(paths.PathFor(companion))
                    .Should().Be(runtimeFolder, $"{companion.FileName} is resolved from the runtime's own folder");
            }

            // And the folder the loader is told to search is that same one, or
            // the files would be together somewhere nothing looks.
            BackendCatalog.SearchAlso(paths.ModelsFolder);
            BackendCatalog.SearchPaths.Should().Contain(paths.ModelsFolder);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // DirectoryNotFoundException is an IOException, so this covers
                // the case where nothing was ever created.
            }
        }
    }

    private static string EmptyFolder() => FolderWith();

    private static string FolderWith(params string[] files)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bt-offer-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, file), "x");
        }

        return folder;
    }
}
