using System;
using System.IO;
using System.Text.Json;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class InstallLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-install-location", Guid.NewGuid().ToString("N"));

    public InstallLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Folder(string leaf)
    {
        var folder = Path.Combine(_root, leaf);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private InstallLocationStore Store(string leaf = "store", string? legacy = null) =>
        new(Path.Combine(_root, leaf, InstallLocationStore.FileName), legacy);

    private static void WriteLegacy(string file, string folder) =>
        File.WriteAllText(file, "{\"modelsFolder\": " + JsonSerializer.Serialize(folder) + "}");

    [Fact]
    public void TheStoreSitsUnderLocalApplicationDataAndNotBesideTheRunningAssembly()
    {
        var file = new AppPaths().InstallLocationFile;

        file.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterTranslator",
            "install-location.json"));

        Path.GetFullPath(file).Should().NotStartWith(Path.GetFullPath(AppContext.BaseDirectory));
    }

    [Fact]
    public void TheStorePathIgnoresTheDataFolderPointer()
    {
        new AppPaths().ProfileRoot.Should().Be(AppPaths.DefaultRoot);
    }

    [Fact]
    public void AChosenFolderIsStillInForceAfterARestart()
    {
        var chosen = Folder("chosen");
        var store = Store();

        var first = new InstallPaths(new AppPaths(Folder("data")), store);
        first.UseFolder(chosen);

        var restarted = new InstallPaths(new AppPaths(Folder("data")), Store());

        restarted.IsDefault.Should().BeFalse();
        restarted.ModelsFolder.Should().Be(chosen);
        restarted.LocationWarning.Should().BeNull();
        File.Exists(store.FilePath).Should().BeTrue();
    }

    [Fact]
    public void TheDefaultAppliesOnlyWhenNothingWasEverStored()
    {
        var data = new AppPaths(Folder("data"));

        var fresh = new InstallPaths(data, Store("never-written"));

        fresh.IsDefault.Should().BeTrue();
        fresh.ModelsFolder.Should().Be(data.ModelsFolder);
        fresh.LocationWarning.Should().BeNull();
    }

    [Fact]
    public void ChoosingTheDefaultAgainIsRemembered()
    {
        var chosen = Folder("chosen");
        var data = new AppPaths(Folder("data"));

        var first = new InstallPaths(data, Store());
        first.UseFolder(chosen);
        first.UseDefault();

        var restarted = new InstallPaths(data, Store());

        restarted.IsDefault.Should().BeTrue();
        restarted.ModelsFolder.Should().Be(data.ModelsFolder);
    }

    [Fact]
    public void ACorruptStoreFallsBackWithoutThrowing()
    {
        var store = Store("corrupt");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ this is not json");

        var data = new AppPaths(Folder("data"));
        var paths = new InstallPaths(data, store);

        paths.IsDefault.Should().BeTrue();
        paths.ModelsFolder.Should().Be(data.ModelsFolder);
        paths.LocationWarning.Should().NotBeNullOrWhiteSpace();
        paths.LocationWarning.Should().Contain(store.FilePath);
    }

    [Fact]
    public void AStoreThatIsNotASettingsDocumentFallsBackWithoutThrowing()
    {
        var store = Store("array");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "[1, 2, 3]");

        var loaded = store.Load();

        loaded.Folder.Should().BeNull();
        loaded.Warning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnUnreachableStoredFolderIsKeptAndReported()
    {
        var gone = Path.Combine(_root, "unplugged-drive", "models");
        var store = Store("unreachable");

        store.Save(gone);

        var paths = new InstallPaths(new AppPaths(Folder("data")), Store("unreachable"));

        paths.IsDefault.Should().BeFalse();
        paths.ModelsFolder.Should().Be(gone);
        paths.LocationWarning.Should().Contain(gone);
        paths.LocationWarning.Should().Contain("not reachable");
    }

    [Fact]
    public void AReachableFolderIsReportedWithoutComplaint()
    {
        var chosen = Folder("writable");

        Store("clean").Save(chosen).Should().BeNull();
    }

    [Fact]
    public void AValueAtTheOldLocationIsCarriedOverOnce()
    {
        var chosen = Folder("legacy-choice");
        var legacy = Path.Combine(Folder("beside-the-executable"), InstallLocationStore.FileName);

        WriteLegacy(legacy, chosen);

        var carried = Store("migrated", legacy).Load();

        carried.Folder.Should().Be(chosen);
        File.Exists(Store("migrated").FilePath).Should().BeTrue();

        WriteLegacy(legacy, Folder("later-legacy-choice"));

        var migrated = Store("migrated", legacy);
        migrated.Save(null);

        migrated.Load().Folder.Should().BeNull("the old location is read once and never again");
    }

    [Fact]
    public void AnEmptyOldLocationChangesNothing()
    {
        var missing = Path.Combine(_root, "no-such-place", InstallLocationStore.FileName);

        Store("untouched", missing).Load().Should().Be(StoredInstallLocation.Unset);
        File.Exists(Store("untouched").FilePath).Should().BeFalse();
    }

    [Fact]
    public void AFailedWriteLeavesThePreviousValueIntact()
    {
        var kept = Folder("kept");
        var store = Store("atomic");

        store.Save(kept).Should().BeNull();

        Directory.CreateDirectory(store.FilePath + ".tmp");

        var complaint = store.Save(Folder("never-lands"));

        complaint.Should().NotBeNullOrWhiteSpace();
        store.Load().Folder.Should().Be(kept);

        Directory.Delete(store.FilePath + ".tmp");
    }

    [Fact]
    public void AWriteLeavesNoStagingFileBehind()
    {
        var store = Store("staging");

        store.Save(Folder("target"));

        File.Exists(store.FilePath).Should().BeTrue();
        File.Exists(store.FilePath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void TheStoreIsReadBeforeTheFolderIsAskedFor()
    {
        var chosen = Folder("chosen-early");
        Store("early").Save(chosen);

        var paths = new InstallPaths(new AppPaths(Folder("data")), Store("early"));

        paths.ModelsFolder.Should().Be(chosen);
        paths.PathFor(new CompanionArtifact
        {
            FileName = "cublas64_13.dll",
            SizeBytes = 1,
            Reason = "test",
        }).Should().Be(Path.Combine(chosen, "cublas64_13.dll"));
    }
}
