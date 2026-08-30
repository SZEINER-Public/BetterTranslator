using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class EffortTierTests : IDisposable
{
    private static readonly string Euro = EffortTiers.Simple.ModelId;

    private static readonly string Gemma = EffortTiers.Thinking.ModelId;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-tiers", Guid.NewGuid().ToString("N"));
    private readonly InstallPaths _paths;
    private int _downloadManagerOpened;

    public EffortTierTests()
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

    private TranslationSettingsViewModel Create() => new(_paths, () => _downloadManagerOpened++);

    private void Install(string id)
    {
        var component = ComponentCatalog.BuiltIn.Single(c => c.Id == id);
        SparseArtifact.Write(_paths.PathFor(component), component.SizeBytes, [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0]);
    }

    private void Remove(string id) =>
        _paths.Remove(ComponentCatalog.BuiltIn.Single(c => c.Id == id)).Should().BeTrue();

    [Fact]
    public void OnlyEuroLlmResolvesSimple()
    {
        var selection = EffortTiers.Resolve([Euro], persisted: null);

        selection.Selected.Should().Be(EffortTiers.Simple);
        selection.Available.Should().ContainSingle().Which.Should().Be(EffortTiers.Simple);
        selection.NothingInstalled.Should().BeFalse();
    }

    [Fact]
    public void OnlyTranslateGemmaResolvesThinking()
    {
        var selection = EffortTiers.Resolve([Gemma], persisted: null);

        selection.Selected.Should().Be(EffortTiers.Thinking);
        selection.Available.Should().ContainSingle().Which.Should().Be(EffortTiers.Thinking);
    }

    [Fact]
    public void BothInstalledDefaultsToThinking()
    {
        var selection = EffortTiers.Resolve([Euro, Gemma], persisted: null);

        selection.Selected.Should().Be(EffortTiers.Thinking);
        selection.Available.Should().HaveCount(2);
        selection.Notice.Should().BeNull();
    }

    [Fact]
    public void NothingInstalledIsTheEmptyState()
    {
        var selection = EffortTiers.Resolve([], persisted: TranslationEffort.Thinking);

        selection.NothingInstalled.Should().BeTrue();
        selection.Selected.Should().BeNull();
        selection.Notice.Should().Be(EffortTiers.NothingInstalledNotice);
    }

    [Fact]
    public void AStalePersistedTierResolvesAndSaysWhy()
    {
        var selection = EffortTiers.Resolve([Euro], persisted: TranslationEffort.Thinking);

        selection.Selected.Should().Be(EffortTiers.Simple);
        selection.Notice.Should().Contain(EffortTiers.Thinking.ModelName);
        selection.Notice.Should().Contain(EffortTiers.Simple.Label);
    }

    [Fact]
    public void APersistedTierThatIsStillInstalledIsKept()
    {
        var selection = EffortTiers.Resolve([Euro, Gemma], persisted: TranslationEffort.Simple);

        selection.Selected.Should().Be(EffortTiers.Simple);
        selection.Notice.Should().BeNull();
    }

    [Fact]
    public void TheLegacyStoredIdentifierMigratesToSimple()
    {
        EffortTiers.Parse("Fast").Should().Be(TranslationEffort.Simple);
        EffortTiers.Parse("Simple").Should().Be(TranslationEffort.Simple);
        EffortTiers.Parse("Thinking").Should().Be(TranslationEffort.Thinking);
        EffortTiers.Parse("nonsense").Should().BeNull();
        EffortTiers.Parse(null).Should().BeNull();
    }

    [Fact]
    public async Task ASettingsRowWrittenBeforeTheRenameStillLoads()
    {
        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        var store = new SettingsStore(database);

        await store.SaveAsync(new AppSettings { Effort = TranslationEffort.Simple }, CancellationToken.None);

        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE settings SET value = 'Fast' WHERE key = 'translation_effort';";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.Effort.Should().Be(TranslationEffort.Simple);
    }

    [Fact]
    public void NoTierIsLabelledFast()
    {
        EffortTiers.All.Should().NotContain(tier =>
            tier.Label.Contains("Fast", StringComparison.OrdinalIgnoreCase));

        EffortTiers.Simple.Label.Should().Be("Simple");
        EffortTiers.Thinking.Label.Should().Be("Thinking");
    }

    [Fact]
    public void EachTierNamesOneCatalogueModelAndNoneClaimsASpeed()
    {
        foreach (var tier in EffortTiers.All)
        {
            var component = ComponentCatalog.BuiltIn.Single(c => c.Id == tier.ModelId);

            component.Name.Should().Be(tier.ModelName);
        }

        ComponentCatalog.BuiltIn.Single(c => c.Id == Euro).Summary
            .Should().NotContainAny("Fast", "fast", "Quick", "quick", "slow");
    }

    [Fact]
    public void WithNothingInstalledNoTierIsActive()
    {
        var vm = Create();

        vm.SelectedEffort.Should().BeNull("a tier whose model is missing is never the active one");
        vm.HasSelectedEffort.Should().BeFalse();
        vm.EffortLabel.Should().Be(EffortTiers.NothingInstalledLabel);
        vm.SelectionNotice.Should().Be(EffortTiers.NothingInstalledNotice);
    }

    [Fact]
    public void WithOnlyEuroLlmInstalledTheSelectionIsSimple()
    {
        Install(Euro);

        var vm = Create();

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Simple);
        vm.EffortLabel.Should().Be("Simple");
        vm.Model.Name.Should().Be(EffortTiers.Simple.ModelName);
    }

    [Fact]
    public void WithOnlyTranslateGemmaInstalledTheSelectionIsThinking()
    {
        Install(Gemma);

        var vm = Create();

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Thinking);
        vm.EffortLabel.Should().Be("Thinking");
        vm.Model.Name.Should().Be(EffortTiers.Thinking.ModelName);
    }

    [Fact]
    public void WithBothInstalledTheDefaultSelectionIsThinking()
    {
        Install(Euro);
        Install(Gemma);

        var vm = Create();

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Thinking);
        vm.SelectionNotice.Should().BeNull();
    }

    [Fact]
    public void AStoredTierWhoseModelIsGoneIsExplainedAndComesBackWhenItIsInstalled()
    {
        Install(Euro);

        var vm = Create();
        var resolved = 0;
        vm.SelectionResolved += _ => resolved++;

        vm.Restore(TranslationEffort.Thinking);

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Simple);
        vm.SelectionNotice.Should().Contain(EffortTiers.Thinking.ModelName);
        resolved.Should().Be(0, "the tier did not move: Simple was already in force");

        Install(Gemma);
        vm.RefreshInstalled();

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Thinking);
        vm.SelectionNotice.Should().BeNull();
        resolved.Should().Be(1, "the inventory moved it, not the reader");
    }

    [Fact]
    public void AnExplicitChoiceSurvivesTheOtherModelArriving()
    {
        Install(Euro);

        var vm = Create();
        var stored = 0;
        vm.EffortChanged += _ => stored++;

        vm.ChooseEffortCommand.Execute(vm.Efforts.Single(o => o.Effort == TranslationEffort.Simple));

        Install(Gemma);
        vm.RefreshInstalled();

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Simple, "the reader chose it");
        stored.Should().Be(0, "choosing the tier already in force stores nothing");
    }

    [Fact]
    public void RemovingTheLastModelClearsTheActiveTier()
    {
        Install(Gemma);

        var vm = Create();
        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Thinking);

        Remove(Gemma);
        vm.RefreshInstalled();

        vm.SelectedEffort.Should().BeNull();
        vm.EffortLabel.Should().Be(EffortTiers.NothingInstalledLabel);
        vm.SelectionNotice.Should().Be(EffortTiers.NothingInstalledNotice);
    }
}
