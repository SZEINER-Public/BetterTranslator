using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.Mcp;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Cli;
using BetterTranslator.Runtime.Agents.Mcp;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What an agent surface is allowed to change out from under the window, and
/// what the window is allowed to change out from under an agent. Both of these
/// went the wrong way: starting the agent server re-applied a backend the shell
/// had deliberately not adopted yet, and any toggle in Settings wrote back a
/// whole row it had read minutes earlier.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class AgentSideEffectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-sideeffect", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AnAgentServerInsideTheApplicationLeavesTheRunningBackendAlone()
    {
        var before = LocalTranslator.Flavor;

        var applied = TranslationGateway.ApplyBackend(
            new AppSettings { RuntimeBackend = RuntimeBackend.Cpu },
            ownsRuntime: false);

        applied.Should().BeFalse();
        LocalTranslator.Flavor.Should().Be(
            before,
            "the window resolved the flavour at startup and asks for a restart to change it; "
            + "an agent server starting up is not that restart");
    }

    [Fact]
    public void TheCommandLineOwnsItsOwnProcessAndDoesApplyTheStoredBackend()
    {
        // ApplyBackend does not pass the stored value through -- it resolves it,
        // and a machine without the CPU library gets a GPU flavour instead. So
        // the expectation is computed the same way, and whatever was in force is
        // put back: this is process-wide state and every later test shares it.
        var backend = LocalTranslator.Backend;

        try
        {
            TranslationGateway
                .ApplyBackend(new AppSettings { RuntimeBackend = RuntimeBackend.Cpu }, ownsRuntime: true)
                .Should().BeTrue("nothing else in bt is going to choose a backend");

            var expected = Path.GetFileNameWithoutExtension(
                BackendCatalog.FileNameFor(new BackendCatalog().Resolve(RuntimeBackend.Cpu)));

            LocalTranslator.Flavor.Should().Be(expected);
        }
        finally
        {
            // The backend goes back, because it decides GpuLayers for every load
            // after this. The flavour string cannot: Prefer has no "nobody has
            // chosen yet" to return to, and it is left holding the name for the
            // backend that was in force -- which is what any later Prefer would
            // have set it to anyway.
            LocalTranslator.Prefer(backend);
            LocalTranslator.Backend.Should().Be(backend, "the suite is left running what it was running");
        }
    }

    [Fact]
    public void TheApplicationsOwnAgentServerIsWiredUpAsAGuest()
    {
        McpServerHost.AppOwnsRuntime.Should().BeFalse();

        new McpServerHost(new NoGuiBridge()).GatewayFactory
            .Should().BeSameAs(McpServerHost.DefaultGateway, "the shipped host opens its gateway the guest way");

        TranslationGateway
            .ApplyBackend(new AppSettings { RuntimeBackend = RuntimeBackend.Cpu }, McpServerHost.AppOwnsRuntime)
            .Should().BeFalse("which is the value the shipped factory passes");
    }

    [Fact]
    public async Task AnUnrelatedToggleInSettingsDoesNotUndoAnAgentsModelChoice()
    {
        var (settings, store) = await ScreenAsync();

        // What select_model does: the same row, written by the other surface,
        // after this screen had already read it.
        var chosen = await store.LoadAsync(CancellationToken.None);
        chosen.SelectedModelPath = @"C:\models\EuroLLM-9B-Instruct-Q4_K_M.gguf";
        chosen.Effort = TranslationEffort.Thinking;
        await store.SaveAsync(chosen, CancellationToken.None);

        settings.LearnFromMyEdits = !settings.LearnFromMyEdits;

        await settings.Written;

        var after = await store.LoadAsync(CancellationToken.None);

        after.SelectedModelPath.Should().Be(
            @"C:\models\EuroLLM-9B-Instruct-Q4_K_M.gguf",
            "the screen never touched the model, so it has no business writing one");
        after.Effort.Should().Be(TranslationEffort.Thinking, "nor the effort that came with it");
    }

    [Fact]
    public async Task RescanningTheModelsFolderIsNotAChoiceAndDoesNotClaimOneWasMade()
    {
        var (screen, _) = await ScreenAsync();

        screen.ModelChosenHere.Should().BeFalse("opening the screen selects a model without anyone choosing it");

        screen.RescanModelsCommand.Execute(null);
        await screen.Written;

        screen.ModelChosenHere.Should().BeFalse(
            "rescanning rebuilds the list and re-selects, which is the screen catching up with the disk");
    }

    [Fact]
    public async Task ChoosingAModelHereIsWrittenAndThenStopsBeingReasserted()
    {
        var (screen, store) = await ScreenAsync();

        var chosen = screen.Models.FirstOrDefault();

        if (chosen is null)
        {
            // Nothing installed on this machine, so there is no click to make.
            // Skipped rather than faked: a fabricated ModelOptionViewModel would
            // assert the test's own wiring, not the screen's.
            return;
        }

        screen.ChooseModelCommand.Execute(chosen);
        await screen.Written;

        (await store.LoadAsync(CancellationToken.None)).SelectedModelPath
            .Should().Be(chosen.Model.Path, "a deliberate click is exactly what should be saved");

        screen.ModelChosenHere.Should().BeFalse("and is spent once written");

        var elsewhere = await store.LoadAsync(CancellationToken.None);
        elsewhere.SelectedModelPath = @"C:\models\Chosen-By-An-Agent.gguf";
        await store.SaveAsync(elsewhere, CancellationToken.None);

        screen.LearnFromMyEdits = !screen.LearnFromMyEdits;
        await screen.Written;

        (await store.LoadAsync(CancellationToken.None)).SelectedModelPath
            .Should().Be(@"C:\models\Chosen-By-An-Agent.gguf", "the click was already honoured; it does not repeat");
    }

    [Fact]
    public async Task TheScreenStillSavesTheFieldsItOwns()
    {
        var (settings, store) = await ScreenAsync();

        var before = (await store.LoadAsync(CancellationToken.None)).UnsureThresholdPercent;

        settings.UnsureThreshold = before + 7;

        await settings.Written;

        (await store.LoadAsync(CancellationToken.None)).UnsureThresholdPercent
            .Should().Be(before + 7);
    }

    [Theory]
    [InlineData("--context-dir", ".")]
    [InlineData("--context-dir=.", null)]
    public void AnOptionThisCommandLineCannotHonourIsAnsweredByNameRatherThanParsed(string option, string? value)
    {
        string[] args = value is null
            ? ["translate", "hi", "--to", "cs", option]
            : ["translate", "hi", "--to", "cs", option, value];

        var command = CommandLine.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Contain("--context-dir").And.Contain("not available");
        command.Error.Should().Contain("application", "the answer says where the capability does live");
    }

    [Fact]
    public void TheHelpDoesNotAdvertiseAnOptionThatCannotBeUsed() =>
        Help.Root.Should().NotContain("--context-dir", "an option listed in the help is one that works");

    private async Task<(SettingsViewModel Screen, SettingsStore Store)> ScreenAsync()
    {
        Directory.CreateDirectory(_root);

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        var store = new SettingsStore(database);

        var settings = new SettingsViewModel(
            store,
            new CacheInspector(paths, database),
            new InstallPaths(paths),
            _ => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => { });

        await settings.LoadAsync(CancellationToken.None);

        return (settings, store);
    }

}
