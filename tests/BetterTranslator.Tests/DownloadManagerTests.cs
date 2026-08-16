using System.Net.Http;
using System.IO;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Real streaming to a real path against a stub server: progress reports, the
/// file landing where it should, and a cancelled or failed run leaving nothing
/// behind that looks installed.
/// </summary>
public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-dl", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstallPaths Paths()
    {
        var paths = new InstallPaths(new AppPaths(_root));
        paths.EnsureCreated();
        return paths;
    }

    private static ModelComponent Component(Uri url, long size) => new()
    {
        Id = "eurollm",
        Name = "EuroLLM",
        Kind = ComponentKind.Model,
        Summary = "test",
        SizeBytes = size,
        Version = "2.0.1",
        DownloadUrl = url,
    };

    [Fact]
    public async Task AComponentStreamsToDiskAndReportsProgress()
    {
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        // A .gguf must now actually be one: the manager verifies the header
        // before promoting, so an error page arriving at the right length cannot
        // be installed. Random bytes would be rejected, correctly.
        payload[0] = 0x47;
        payload[1] = 0x47;
        payload[2] = 0x55;
        payload[3] = 0x46;
        payload[4] = 3;
        payload[5] = 0;
        payload[6] = 0;
        payload[7] = 0;

        using var server = new StubHttpServer((path, body) => (200, "application/octet-stream", payload));

        var paths = Paths();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths);

        var progress = new SyncProgress<DownloadProgress>();
        var component = Component(new Uri(server.BaseAddress, "/eurollm.gguf"), payload.Length);

        var result = await manager.DownloadAsync(component, progress, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        result.BytesSoFar.Should().Be(payload.Length);

        paths.IsInstalled(component).Should().BeTrue();
        (await File.ReadAllBytesAsync(paths.PathFor(component))).Should().Equal(payload);

        // No .part is left beside the finished file.
        File.Exists(paths.PathFor(component) + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task AFailedResponseIsReportedWithoutLeavingAFile()
    {
        using var server = new StubHttpServer((path, body) => (404, "text/plain", "gone"u8.ToArray()));

        var paths = Paths();
        using var client = server.CreateClient();
        var component = Component(new Uri(server.BaseAddress, "/missing.gguf"), 100);

        var result = await new DownloadManager(client, paths)
            .DownloadAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().Contain("404");
        paths.IsInstalled(component).Should().BeFalse();
    }

    [Fact]
    public async Task AComponentWithNoLinkFailsWithoutAttemptingATransfer()
    {
        var paths = Paths();
        using var client = new HttpClient();

        var component = new ModelComponent
        {
            Id = "eurollm",
            Name = "EuroLLM",
            Kind = ComponentKind.Model,
            Summary = "test",
            SizeBytes = 100,
            Version = "2.0.1",
        };

        var result = await new DownloadManager(client, paths)
            .DownloadAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().Contain("No download link");
    }

    [Fact]
    public void EveryFlavourIsListedAndTheOnesThatCannotRunSayWhy()
    {
        // This used to assert the opposite: the list was filtered by the probe,
        // so a Radeon never saw a CUDA row at all. That left the installer and
        // the settings screen disagreeing -- BackendCatalog.Options has always
        // returned all three and marked the unrunnable one -- so the filter went
        // and the row is marked instead. What the filter protected against, a
        // download that can never start, is now handled by locking the row.
        var amd = ComponentCatalog.RuntimesFor(new HardwareReport(false, true, CanUseVulkan: true, CanUseCuda: false));

        amd.Select(r => r.Id).Should().BeEquivalentTo(
            ["betterruntime-cpu", "betterruntime-vulkan", "betterruntime-cuda"]);

        amd.Single(r => r.Id == "betterruntime-cuda").IsSupported.Should().BeFalse();
        amd.Single(r => r.Id == "betterruntime-cuda").UnsupportedReason.Should().Contain("NVIDIA");
        amd.Single(r => r.Id == "betterruntime-vulkan").IsSupported.Should().BeTrue();

        var nvidia = ComponentCatalog.RuntimesFor(new HardwareReport(true, false, CanUseVulkan: false, CanUseCuda: true));
        nvidia.Single(r => r.Id == "betterruntime-cuda").IsSupported.Should().BeTrue();
        nvidia.Single(r => r.Id == "betterruntime-vulkan").IsSupported.Should().BeFalse();

        // CPU is the floor: runnable on any machine, whatever the probe found.
        var nothing = ComponentCatalog.RuntimesFor(new HardwareReport(false, false, false, false));
        nothing.Should().HaveCount(3);
        nothing.Single(r => r.Id == "betterruntime-cpu").IsSupported.Should().BeTrue();
        nothing.Where(r => r.Id != "betterruntime-cpu").Should().AllSatisfy(
            r => r.IsSupported.Should().BeFalse());

        // Every flavour can be ticked off, and several can be ticked on. The old
        // invariant -- one locked runtime row -- would have left a machine that
        // can run Vulkan unable to decline the CPU build.
        amd.Should().AllSatisfy(r => r.IsRequired.Should().BeFalse());
        amd.Single(r => r.Id == "betterruntime-cpu").PreSelected.Should().BeTrue();

        // A flavour the machine cannot run is never pre-ticked, whatever else is
        // true of it: the installer would otherwise open with it selected.
        amd.Where(r => !r.IsSupported).Should().AllSatisfy(r => r.PreSelected.Should().BeFalse());
    }

    [Fact]
    public void AModelIsRemovable()
    {
        var paths = Paths();
        var model = ComponentCatalog.BuiltIn.First(c => c.Kind == ComponentKind.Model);

        File.WriteAllBytes(paths.PathFor(model), [1, 2, 3]);

        paths.Remove(model).Should().BeTrue();
        paths.IsInstalled(model).Should().BeFalse();
    }

    [Fact]
    public void CancellingTheFolderDialogLeavesTheDefaultInPlace()
    {
        var paths = Paths();

        // Nothing calls UseFolder when the dialog is cancelled.
        paths.IsDefault.Should().BeTrue();
        paths.ModelsFolder.Should().Be(paths.DefaultModelsFolder);

        paths.UseFolder(Path.Combine(_root, "elsewhere"));
        paths.IsDefault.Should().BeFalse();

        paths.UseDefault();
        paths.ModelsFolder.Should().Be(paths.DefaultModelsFolder);
    }

    [Fact]
    public void TheActionButtonPricesTheRealSelection()
    {
        var runtime = ComponentCatalog
            .RuntimesFor(new HardwareReport(false, false, false, false))
            .Single(r => r.Id == "betterruntime-cpu");

        var euro = ComponentCatalog.BuiltIn.Single(c => c.Id == "eurollm");
        var gemma = ComponentCatalog.BuiltIn.Single(c => c.Id == "translategemma");

        // Runtime plus EuroLLM is what the first-run list pre-selects. The figure is
        // 5.2 GB because that is what the server reports for the real artifact;
        // the 1.7 GB this once asserted was an estimate made before one existed.
        ByteSize.Format(ComponentCatalog.TotalBytes([runtime, euro]), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("5.2 GB");

        ByteSize.Format(ComponentCatalog.TotalBytes([runtime, euro, gemma]), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("7.5 GB");
    }

    [Fact]
    public void APastedZipIsNotAddedToTheCatalogue()
    {
        var catalog = new ComponentCatalog();

        var result = catalog.AddCustom("https://example.com/weights.zip");

        result.IsAccepted.Should().BeFalse();
        catalog.Custom.Should().BeEmpty();
    }

    [Fact]
    public void APastedModelIsAddedAndTaggedAsCustom()
    {
        var catalog = new ComponentCatalog();

        catalog.AddCustom("https://huggingface.co/owner/model").IsAccepted.Should().BeTrue();

        var added = catalog.Custom.Single();
        added.IsCustom.Should().BeTrue();
        added.Summary.Should().Be("added by you");
        added.Name.Should().Be("owner/model");

        catalog.RemoveCustom(added.Id).Should().BeTrue();
        catalog.Custom.Should().BeEmpty();
    }
}
