using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class ModelSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-model", Guid.NewGuid().ToString("N"));
    private readonly InstallPaths _paths;
    private readonly ModelLibrary _library = new();

    public ModelSelectionTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new InstallPaths(new AppPaths(_root));
        _paths.EnsureCreated();
    }

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

    private static ModelComponent Component(string id) => ComponentCatalog.BuiltIn.Single(c => c.Id == id);

    private static string Euro => Component("eurollm").FileName;

    private static string Gemma => Component("translategemma").FileName;

    private string Install(string id)
    {
        var component = Component(id);
        var path = _paths.PathFor(component);

        SparseArtifact.Write(path, component.SizeBytes, [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0]);

        return path;
    }

    private void Remove(string id) => File.Delete(_paths.PathFor(Component(id)));

    private System.Collections.Generic.IReadOnlyList<LocalModel> Scan() => _library.Scan(_paths.ModelsFolder);

    [Fact]
    public void AModelChosenBeforeShutdownIsTheOneInForceAfterTheNextStart()
    {
        var euro = Install("eurollm");
        Install("translategemma");

        var choice = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: ModelSelection.FileNameOf(euro),
            chosenPath: euro,
            chosenExplicitly: true,
            tierFileName: Gemma);

        choice.Path.Should().Be(euro, "the stored choice outranks the effort's own model");
        choice.Reason.Should().BeNull();
    }

    [Fact]
    public void TheStoredIdentifierIsTheFileNameAndSurvivesTheFolderMoving()
    {
        var euro = Install("eurollm");
        Install("translategemma");

        var moved = Path.Combine(_root, "somewhere-else", Path.GetFileName(euro));

        var choice = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: Euro,
            chosenPath: moved,
            chosenExplicitly: true,
            tierFileName: Gemma);

        choice.Path.Should().Be(euro, "the file name is what is matched, not the path it was stored under");

        ModelSelection.FileNameOf(euro).Should().Be(Euro);
        Euro.Should().NotContain(Path.DirectorySeparatorChar.ToString());
    }

    [Fact]
    public void APersistedModelThatIsGoneFallsBackAndSaysWhy()
    {
        Install("eurollm");
        var gemma = Install("translategemma");

        Remove("eurollm");

        var choice = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: Euro,
            chosenPath: Path.Combine(_paths.ModelsFolder, Euro),
            chosenExplicitly: true,
            tierFileName: Gemma);

        choice.Path.Should().Be(gemma);
        choice.Reason.Should().Be(ModelSelection.ChosenModelGone);
    }

    [Fact]
    public void APersistedModelThatIsGoneWithNoTierModelEitherSaysSo()
    {
        var euro = Install("eurollm");

        var choice = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: "something-nobody-has.gguf",
            chosenPath: null,
            chosenExplicitly: true,
            tierFileName: Gemma);

        choice.Path.Should().Be(euro);
        choice.Reason.Should().Be(ModelSelection.ChosenModelGoneWithNoTier);
    }

    [Fact]
    public void ThePresetIsAppliedOnlyWhenNothingWasEverChosen()
    {
        Install("eurollm");
        var gemma = Install("translategemma");

        var choice = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: null,
            chosenPath: null,
            chosenExplicitly: false,
            tierFileName: Gemma);

        choice.Path.Should().Be(gemma);
        choice.Reason.Should().BeNull();
    }

    [Fact]
    public void AnEffortSwitchTakesTheModelWithItRatherThanKeepingTheOldChoice()
    {
        var euro = Install("eurollm");
        var gemma = Install("translategemma");

        var afterSwitch = ModelSelection.Resolve(
            _library,
            Scan(),
            chosenFileName: Euro,
            chosenPath: euro,
            chosenExplicitly: false,
            tierFileName: Gemma);

        afterSwitch.Path.Should().Be(gemma, "switching the effort is a later choice than the picker's");
    }

    [Fact]
    public void AScanThatAnsweredNothingChangesNothing()
    {
        var choice = ModelSelection.Resolve(
            _library,
            [],
            chosenFileName: Euro,
            chosenPath: Path.Combine(_paths.ModelsFolder, Euro),
            chosenExplicitly: true,
            tierFileName: Gemma);

        choice.HasModel.Should().BeFalse();
        choice.Reason.Should().BeNull("a probe that has not answered is not a reason to report a missing model");
    }

    [Fact]
    public async Task TheChoiceSurvivesARestartThroughTheSettingsStore()
    {
        var euro = Install("eurollm");
        Install("translategemma");

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        var store = new SettingsStore(database);

        var first = await store.LoadAsync(CancellationToken.None);
        first.SelectedModelPath = euro;
        first.SelectedModelFile = ModelSelection.FileNameOf(euro);
        first.ModelChosenExplicitly = true;

        await store.SaveAsync(first, CancellationToken.None);

        var second = await store.LoadAsync(CancellationToken.None);

        second.SelectedModelFile.Should().Be(Euro);
        second.ModelChosenExplicitly.Should().BeTrue();

        ModelSelection.Resolve(
            _library,
            Scan(),
            second.SelectedModelFile,
            second.SelectedModelPath,
            second.ModelChosenExplicitly,
            Gemma).Path.Should().Be(euro);
    }

    [Fact]
    public async Task ARowWrittenBeforeTheIdentifierExistedStillNamesItsModel()
    {
        var euro = Install("eurollm");

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        var store = new SettingsStore(database);

        await store.SaveAsync(new AppSettings { SelectedModelPath = euro }, CancellationToken.None);

        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM settings WHERE key IN ('selected_model_file', 'selected_model_explicit');";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.SelectedModelFile.Should().Be(Euro, "the identifier is derived from the path a older build stored");
        loaded.ModelChosenExplicitly.Should().BeTrue("a stored path was only ever written by a real choice");
    }

    [Fact]
    public async Task NoStoredModelLeavesTheChoiceUnmadeRatherThanGuessed()
    {
        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        var loaded = await new SettingsStore(database).LoadAsync(CancellationToken.None);

        loaded.SelectedModelPath.Should().BeEmpty();
        loaded.SelectedModelFile.Should().BeEmpty();
        loaded.ModelChosenExplicitly.Should().BeFalse();
    }
}
