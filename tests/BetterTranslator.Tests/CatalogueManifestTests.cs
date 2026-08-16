using System.Linq;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The catalogue and the manifest have to agree about every artifact they both
/// describe.
///
/// The defect this exists for: swapping a row's download link is three edits --
/// the id, the byte length and the file name -- and doing only the first leaves
/// a row that downloads real bytes and then fails its own length check, or lands
/// under a name the presence check will never find. Nothing else in the suite
/// notices, because both files are real and both halves are individually
/// plausible.
/// </summary>
public sealed class CatalogueManifestTests
{
    /// <summary>Every component the catalogue can offer, on any hardware.</summary>
    private static IEnumerable<ModelComponent> Offerable() =>
        ComponentCatalog.BuiltIn.Concat(
            ComponentCatalog.RuntimesFor(new HardwareReport(true, true, true, true)));

    [Fact]
    public void EveryDownloadableRowAgreesWithTheManifest()
    {
        var checkedRows = 0;

        foreach (var component in Offerable().Where(c => c.DownloadUrl is not null))
        {
            var fileId = DriveIdOf(component.DownloadUrl!);
            fileId.Should().NotBeNull($"{component.Name} must carry a Drive download link");

            var artifact = ArtifactManifest.For(fileId!);
            artifact.Should().NotBeNull(
                $"{component.Name} points at Drive id {fileId}, which is not in the manifest -- "
                + "a download would have no length or hash to verify against");

            artifact!.SizeBytes.Should().Be(component.SizeBytes,
                $"{component.Name}'s advertised size must be the manifest's, or every download fails its length check");

            artifact.FileName.Should().Be(component.FileName,
                $"{component.Name} must land under the name the server reports, or the presence check "
                + "fetches a model that is already on disk");

            checkedRows++;
        }

        checkedRows.Should().BeGreaterThan(0, "the catalogue must offer something fetchable");
    }

    [Fact]
    public void TheTwoTranslateGemmaBuildsAreNotConflated()
    {
        // They differ by 16,224 bytes and by a hyphen where the other has a dot.
        // A looser match would treat one as the other, and the presence check
        // would report a model as installed that is not the one that would be
        // downloaded.
        var hyphen = ArtifactManifest.For("15Yt13K9DhtwnHvTHFqh4LYKq6on_KQvr");
        var dot = ArtifactManifest.For("1LM7u2jN8BLXMPgY8--Z3gz8E-DweEYAe");

        hyphen.Should().NotBeNull();
        dot.Should().NotBeNull();

        hyphen!.FileName.Should().NotBe(dot!.FileName);
        hyphen.SizeBytes.Should().NotBe(dot.SizeBytes);
        (hyphen.SizeBytes - dot.SizeBytes).Should().Be(16224);
    }

    [Fact]
    public void TheCatalogueOffersTheBuildItsLinkActuallyServes()
    {
        var gemma = ComponentCatalog.BuiltIn.Single(c => c.Id == "translategemma");

        // Measured from the link on 2026-08-02: Content-Range total and the
        // Content-Disposition filename, not figures carried over from the row
        // this replaced.
        gemma.SizeBytes.Should().Be(2489909312);
        gemma.FileName.Should().Be("translategemma-4b-it-Q4_K_M.gguf");
        DriveIdOf(gemma.DownloadUrl!).Should().Be("15Yt13K9DhtwnHvTHFqh4LYKq6on_KQvr");
    }

    [Fact]
    public void TheCudaRowCarriesTheArtifactItsLinkActuallyServes()
    {
        var cuda = ComponentCatalog
            .RuntimesFor(new HardwareReport(true, false, CanUseVulkan: false, CanUseCuda: true))
            .Single(c => c.Id == "betterruntime-cuda");

        // Measured from the link on 2026-08-08: the Content-Range total and the
        // Content-Disposition filename. The 560 MB this row carried was an
        // estimate made before a CUDA build existed, and at four times the real
        // figure it would have failed the length check on every download.
        cuda.SizeBytes.Should().Be(145155584);
        cuda.FileName.Should().Be("BetterRuntimeCUDA.dll");
        DriveIdOf(cuda.DownloadUrl!).Should().Be("1ipcWlhbPl5JCWI8Lt1Krxt6zclpdxRGp");

        // The name the loader searches for and the name the download lands under
        // have to be one string, not two that happen to agree.
        cuda.FileName.Should().Be(BackendCatalog.FileNameFor(RuntimeBackend.Cuda));
    }

    [Fact]
    public void EveryRuntimeFlavourIsFetchableEvenWhereItCannotRun()
    {
        // Being unable to run a flavour and being unable to fetch it are
        // different facts. The row is offered with a real link and a real size
        // on every machine; what the hardware decides is whether it can be
        // ticked, which is asserted on the installer row rather than here.
        foreach (var runtime in ComponentCatalog.RuntimesFor(new HardwareReport(false, false, false, false)))
        {
            runtime.DownloadUrl.Should().NotBeNull($"{runtime.Name} has been built and measured");
            ArtifactManifest.ForFileName(runtime.FileName).Should().NotBeNull(
                $"{runtime.Name} carries a link, so it needs an integrity row");
        }
    }

    /// <summary>
    /// The same invariant B30 added for components, extended to the files that
    /// have to land beside one.
    ///
    /// A companion declares its own length and hash so the download can be
    /// verified without a manifest lookup, and the manifest declares them again.
    /// Two sources of truth for one file is exactly the shape that let a link be
    /// swapped without its length, so they are held equal here rather than
    /// trusted to stay that way.
    /// </summary>
    [Fact]
    public void EveryCompanionAgreesWithTheManifest()
    {
        var companions = ComponentCatalog
            .RuntimesFor(new HardwareReport(true, true, true, true))
            .SelectMany(c => c.Companions)
            .ToList();

        companions.Should().NotBeEmpty("the CUDA runtime cannot load without its cuBLAS libraries");

        foreach (var companion in companions)
        {
            var artifact = ArtifactManifest.ForFileName(companion.FileName);

            artifact.Should().NotBeNull(
                $"{companion.FileName} is fetched, so it needs an integrity row");

            artifact!.SizeBytes.Should().Be(companion.SizeBytes,
                $"{companion.FileName}'s declared length must be the manifest's, or its download fails the length check");

            artifact.Sha256.Should().Be(companion.Sha256,
                $"{companion.FileName}'s declared hash must be the manifest's");

            companion.Sha256.Should().NotBeNullOrWhiteSpace(
                $"{companion.FileName} was read off disk, so its contents can be checked and not merely its length");

            DriveIdOf(companion.DownloadUrl!).Should().Be(artifact.FileId,
                $"{companion.FileName} must be fetched from the id the manifest verifies it against");
        }
    }

    /// <summary>
    /// The catalogue says which files to fetch; the loader says which files must
    /// be present to count as installed. Two lists of the same fact, and if they
    /// drift the installer downloads one set while the settings screen checks
    /// for another -- a component that installs successfully and still reports
    /// itself missing, or worse, the reverse.
    /// </summary>
    [Fact]
    public void WhatIsFetchedIsWhatTheLoaderRequires()
    {
        foreach (var backend in new[] { RuntimeBackend.Cpu, RuntimeBackend.Vulkan, RuntimeBackend.Cuda })
        {
            var component = ComponentCatalog
                .RuntimesFor(new HardwareReport(true, true, true, true))
                .Single(c => c.Id == "betterruntime-" + backend.ToString().ToLowerInvariant());

            component.Companions.Select(c => c.FileName).Should().BeEquivalentTo(
                BackendCatalog.DependenciesFor(backend),
                $"what {component.Name} downloads must be what the loader looks for");
        }
    }

    [Fact]
    public void TheCudaRuntimeCanBeInstalledInAWorkingState()
    {
        var cuda = ComponentCatalog
            .RuntimesFor(new HardwareReport(true, false, CanUseVulkan: false, CanUseCuda: true))
            .Single(c => c.Id == "betterruntime-cuda");

        // The whole of B47 in one assertion: the DLL alone is not installable,
        // because on its own it cannot load and the failure is silent.
        cuda.IsFetchable.Should().BeTrue("its cuBLAS libraries now have somewhere to come from");
        cuda.Companions.Should().HaveCount(2);
        cuda.InstallBytes.Should().Be(661508832);
    }

    [Fact]
    public void EveryManifestRowIsDistinct()
    {
        ArtifactManifest.All.Select(a => a.FileId).Should().OnlyHaveUniqueItems();
        ArtifactManifest.All.Select(a => a.FileName).Should().OnlyHaveUniqueItems();
    }

    private static string? DriveIdOf(Uri url)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);

            if (split.Length == 2 && split[0].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                return split[1];
            }
        }

        return null;
    }
}
