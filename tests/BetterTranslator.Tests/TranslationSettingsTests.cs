using System.IO;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// S8. With a model absent, both routes to it are blocked and both reach the
/// download manager: the effort menu item and the Advanced radio.
/// </summary>
public sealed class TranslationSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-settings", Guid.NewGuid().ToString("N"));
    private readonly InstallPaths _paths;
    private int _downloadManagerOpened;

    public TranslationSettingsTests()
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

    /// <summary>
    /// A stub of the declared length, not a three-byte placeholder. Installed
    /// state is decided on the length now, so a short file is a partial
    /// download and rightly does not count as installed.
    /// </summary>
    private void Install(string id)
    {
        var component = ComponentCatalog.BuiltIn.Single(c => c.Id == id);
        SparseArtifact.Write(_paths.PathFor(component), component.SizeBytes, [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0]);
    }

    [Fact]
    public void WithNothingInstalledBothEffortsAreBlockedAndNameTheirModel()
    {
        var vm = Create();

        vm.Efforts.Should().OnlyContain(e => e.IsBlocked);

        var thinking = vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking);
        thinking.InstallHint.Should().Be("Install");
        thinking.BlockedTooltip.Should().Be("TranslateGemma is not installed");
        thinking.RadioNote.Should().Be("Not installed. Download it to use Thinking.");
    }

    [Fact]
    public void ChoosingABlockedEffortDoesNotSwitchAndOpensTheDownloadManager()
    {
        Install("eurollm");
        var vm = Create();
        vm.RefreshInstalled();

        var before = vm.SelectedEffort;
        var thinking = vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking);

        vm.ChooseEffortCommand.Execute(thinking);

        vm.SelectedEffort.Should().BeSameAs(before, "a blocked effort does not switch");
        _downloadManagerOpened.Should().Be(1);
    }

    [Fact]
    public void TheAdvancedGetItButtonAlsoReachesTheDownloadManager()
    {
        var vm = Create();
        var thinking = vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking);

        vm.GetModelCommand.Execute(thinking);

        _downloadManagerOpened.Should().Be(1, "the radio's Get it is the second route to the same place");
    }

    [Fact]
    public void AnInstalledEffortSwitchesNormally()
    {
        Install("eurollm");
        Install("translategemma");

        var vm = Create();
        vm.RefreshInstalled();

        var thinking = vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking);
        vm.ChooseEffortCommand.Execute(thinking);

        vm.SelectedEffort!.Effort.Should().Be(TranslationEffort.Thinking);
        _downloadManagerOpened.Should().Be(0);
        thinking.InstallHint.Should().BeNull();
        thinking.RadioNote.Should().BeNull();
    }

    [Fact]
    public void RemovingAModelBlocksItsRoutesAgain()
    {
        Install("translategemma");
        var vm = Create();
        vm.RefreshInstalled();

        var thinking = vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking);
        thinking.IsBlocked.Should().BeFalse();

        _paths.Remove(thinking.Model).Should().BeTrue();
        vm.RefreshInstalled();

        thinking.IsBlocked.Should().BeTrue();
        thinking.BlockedTooltip.Should().Contain("TranslateGemma");
    }

    [Fact]
    public void SimpleIsEuroLlmAndThinkingIsTranslateGemma()
    {
        var vm = Create();

        vm.Efforts.Single(e => e.Effort == TranslationEffort.Simple).Model.Name.Should().Be("EuroLLM");
        vm.Efforts.Single(e => e.Effort == TranslationEffort.Thinking).Model.Name.Should().Be("TranslateGemma");
    }

    [Fact]
    public void ResetRestoresTheDefaultsAndPicksAnAvailableModel()
    {
        Install("translategemma");
        var vm = Create();
        vm.RefreshInstalled();

        vm.Temperature = 0.9;
        vm.UserPrompt = "keep product names in English";

        vm.ResetToDefaultsCommand.Execute(null);

        vm.Temperature.Should().Be(0.2);
        vm.UserPrompt.Should().BeEmpty();
        vm.SelectedEffort!.IsInstalled.Should().BeTrue("reset does not land on a model that is not there");
    }

    [Fact]
    public void TheAdvancedNotesAreTheDocumentedStrings()
    {
        // The temperature note belongs to the model now: it carries the reason
        // when the chosen one writes its own sampler and ignores the slider.
        var vm = Create();

        vm.SelectedEffort = vm.Efforts.Single(option => option.Effort == TranslationEffort.Simple);

        vm.TemperatureNote.Should().Be("Low keeps wording close to the source.");

        TranslationSettingsViewModel.UserPromptPlaceholder
            .Should().Be("Sent with every request. Example: keep product names in English.");
    }
}
