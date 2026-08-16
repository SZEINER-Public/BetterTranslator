using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Removing a runtime from Settings, and the two cases where it cannot be done.
///
/// The defect these exist for: Delete caught its own IOException and said
/// nothing. Pressing it on the runtime the session had loaded refreshed the list,
/// left the row exactly where it was, and left the file on disk -- so it read as
/// a button that does nothing, and the file was still there after a restart.
/// </summary>
public sealed class RuntimeRemovalTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-remove", Guid.NewGuid().ToString("N"));
    private SettingsViewModel _settings = null!;
    private InstallPaths _paths = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var appPaths = new AppPaths(_root);
        appPaths.EnsureCreated();

        var database = new Database(appPaths);
        await database.MigrateAsync(CancellationToken.None);

        _paths = new InstallPaths(appPaths);
        _paths.EnsureCreated();

        _settings = new SettingsViewModel(
            new SettingsStore(database),
            new CacheInspector(appPaths, database),
            _paths,
            _ => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => { });

        await _settings.LoadAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private static InstalledModelRow RowFor(ModelComponent component) =>
        new(component.Id, component.Name, "Runtime", "3.4 MB", CanDelete: true);

    private static ModelComponent Runtime(RuntimeBackend backend) =>
        ComponentCatalog
            .RuntimesFor(new HardwareReport(true, true, true, true))
            .Single(c => c.Id == "betterruntime-" + backend.ToString().ToLowerInvariant());

    [Fact]
    public async Task TheRuntimeThisSessionLoadedRefusesAndSaysWhy()
    {
        _settings.RunningBackend = RuntimeBackend.Cpu;

        await _settings.DeleteModelCommand.ExecuteAsync(RowFor(Runtime(RuntimeBackend.Cpu)));

        _settings.HasDataMessage.Should().BeTrue("a press that cannot work must not look like one that did");
        _settings.DataMessage.Should().Contain("will not delete a library")
            .And.Contain("restart", "the message has to say what would actually make it removable");
    }

    [Fact]
    public async Task TheLastRuntimeRefusesRatherThanLeavingNothingAbleToTranslate()
    {
        // Running on Vulkan, so the loaded-library rule does not apply to the
        // CPU row and the next rule is the one under test.
        _settings.RunningBackend = RuntimeBackend.Vulkan;

        await _settings.DeleteModelCommand.ExecuteAsync(RowFor(Runtime(RuntimeBackend.Cpu)));

        var installedRuntimes = ComponentCatalog
            .RuntimesFor(new HardwareReport(true, true, true, true))
            .Count(c => File.Exists(Path.Combine(AppContext.BaseDirectory, c.FileName))
                     || File.Exists(_paths.PathFor(c)));

        _settings.HasDataMessage.Should().BeTrue();

        if (installedRuntimes <= 1)
        {
            _settings.DataMessage.Should().Contain("only runtime installed");
        }
        else
        {
            // More than one on this machine, so the refusal does not apply and
            // the message reports what happened instead. Asserted both ways
            // rather than assuming the developer's machine.
            _settings.DataMessage.Should().NotContain("only runtime installed");
        }
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotThereSaysSoRatherThanNothing()
    {
        var model = ComponentCatalog.BuiltIn.First(c => c.Kind == ComponentKind.Model);

        File.Exists(_paths.PathFor(model)).Should().BeFalse("the temp folder starts empty");

        await _settings.DeleteModelCommand.ExecuteAsync(
            new InstalledModelRow(model.Id, model.Name, "Model", "2.3 GB", CanDelete: true));

        _settings.DataMessage.Should().Contain("not on this machine");
    }

    [Fact]
    public async Task AModelThatIsThereIsActuallyDeleted()
    {
        // The path that always worked, kept honest: the file goes, and the
        // message names where it went from.
        var model = ComponentCatalog.BuiltIn.First(c => c.Kind == ComponentKind.Model);
        var path = _paths.PathFor(model);

        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await _settings.DeleteModelCommand.ExecuteAsync(
            new InstalledModelRow(model.Id, model.Name, "Model", "2.3 GB", CanDelete: true));

        File.Exists(path).Should().BeFalse("a model is not loaded, so nothing stops it being removed");
        _settings.DataMessage.Should().StartWith("Removed ");
    }
}
