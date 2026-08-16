using System.IO;
using System.Linq;
using System.Threading;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

[Collection(EngineConfigCollection.Name)]
public sealed class TranslationDirectionTests(ITestOutputHelper output) : IDisposable
{
    private const string Gemma = "translategemma-4b-it-Q4_K_M";
    private const string Euro = "EuroLLM-9B-Instruct-Q4_K_M";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-direction", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
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
    public void ExplicitSourceWithExplicitTargetReachesTheEngineUnchanged()
    {
        var workspace = Workspace();

        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "cs"));
        workspace.ChooseLanguageCommand.Execute(Row(workspace, "en"));

        var job = new TranslationJob
        {
            Text = "Ahoj svete.",
            ModelPath = "model.gguf",
            Direction = workspace.Direction,
        };

        job.From.Code.Should().Be("cs");
        job.To.Code.Should().Be("en");
        job.IsSameLanguage.Should().BeFalse();
    }

    [Fact]
    public void AutoSourceTakesTheDetectedLanguageWhileNothingWasChosen()
    {
        var workspace = Workspace();

        workspace.DetectSource = _ => LanguageChoice.Of("cs", "Czech", "Latn");
        workspace.DetectSourceLanguage("Ahoj svete.");

        workspace.SourceLanguage.Code.Should().Be("cs");
        workspace.Direction.Source.Code.Value.Should().Be("cs");
    }

    [Fact]
    public void DetectionNeverOverwritesASourceTheReaderChose()
    {
        var workspace = Workspace();

        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "de"));
        workspace.DetectSource = _ => LanguageChoice.Of("cs", "Czech", "Latn");
        workspace.DetectSourceLanguage("Ahoj svete.");

        workspace.SourceLanguage.Code.Should().Be("de");
    }

    [Fact]
    public void DetectionOnAnEmptyBufferChangesNothing()
    {
        var workspace = Workspace();
        var asked = 0;

        workspace.DetectSource = _ =>
        {
            asked++;
            return LanguageChoice.Of("cs", "Czech", "Latn");
        };

        workspace.DetectSourceLanguage("   ");

        asked.Should().Be(0);
        workspace.SourceLanguage.Code.Should().Be("en");
    }

    [Fact]
    public void ARegionVariantOnOneSideIsStillTheSameLanguageForTheGuard()
    {
        var direction = TranslationDirection.Between("en-GB", "English", "en-US", "English");

        direction.IsSameLanguage.Should().BeTrue();
        new DirectionGuard().Inspect(direction).Status.Should().Be(DirectionStatus.SameLanguage);
    }

    [Fact]
    public void ChineseInTheTwoScriptsIsNotOneLanguage()
    {
        var direction = TranslationDirection.Of(
            LanguageChoice.Of("zh-CN", "Chinese (Simplified)", "Hans"),
            LanguageChoice.Of("zh-TW", "Chinese (Traditional)", "Hant"));

        direction.IsSameLanguage.Should().BeFalse();
        new DirectionGuard().Inspect(direction).Status.Should().Be(DirectionStatus.Ready);
    }

    [Fact]
    public async Task AnIdenticalPairIsAStateAndTheEngineThrowsNothingForIt()
    {
        using var translator = new LocalTranslator();

        var job = new TranslationJob
        {
            Text = "The build is green.",
            ModelPath = "model.gguf",
            Direction = TranslationDirection.Between("en", "English", "en-GB", "English"),
        };

        var outcome = await translator.TranslateAsync(job, CancellationToken.None);

        outcome.Direction!.Status.Should().Be(DirectionStatus.SameLanguage);
        outcome.HasText.Should().BeFalse();
    }

    [Fact]
    public void AnIdenticalPairBlocksSendingAndOffersTheSwap()
    {
        var workspace = Workspace();

        workspace.Draft = "The build is green.";
        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "en"));
        workspace.ChooseLanguageCommand.Execute(Row(workspace, "en"));

        workspace.DirectionVerdict.Status.Should().Be(DirectionStatus.SameLanguage);
        workspace.CanSend.Should().BeFalse();
        workspace.HasDirectionReason.Should().BeTrue();
        workspace.CanSwapDirection.Should().BeTrue();
    }

    [Fact]
    public void AnUnknownSourceIsRefusedRatherThanFilledInFromTheTarget()
    {
        var workspace = Workspace();

        workspace.DetectSource = _ => LanguageChoice.Unknown;
        workspace.DetectSourceLanguage("????");

        workspace.SourceLanguage.IsUnknown.Should().BeTrue();
        workspace.Direction.IsSameLanguage.Should().BeFalse();
        workspace.DirectionVerdict.Status.Should().Be(DirectionStatus.UnknownSource);
        workspace.CanSend.Should().BeFalse();
    }

    [Fact]
    public void SwapWritesBothSidesInOneOperation()
    {
        var workspace = Workspace();

        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "cs"));
        workspace.ChooseLanguageCommand.Execute(Row(workspace, "de"));
        workspace.SwapDirectionCommand.Execute(null);

        workspace.Direction.Source.Code.Value.Should().Be("de");
        workspace.Direction.Target.Code.Value.Should().Be("cs");
    }

    [Fact]
    public void AModelSwitchThatInvalidatesOneSideKeepsThePairAndNamesTheSide()
    {
        var workspace = Workspace();

        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "en"));
        workspace.ChooseLanguageCommand.Execute(Row(workspace, "th"));

        SelectModel(workspace, Euro, "EuroLLM");

        output.WriteLine($"{workspace.DirectionVerdict.Status}: {workspace.DirectionReason}");

        workspace.DirectionVerdict.Status.Should().Be(DirectionStatus.TargetUnsupported);
        workspace.DirectionVerdict.LanguageName.Should().Be("Thai");
        workspace.DirectionVerdict.ModelName.Should().Be("EuroLLM");
        workspace.CanSend.Should().BeFalse();
        workspace.Direction.Target.Code.Value.Should().Be("th", "the pair is not rewritten by a model switch");

        SelectModel(workspace, Gemma, "TranslateGemma");

        workspace.DirectionVerdict.CanSend.Should().BeTrue();
        workspace.Direction.Target.Code.Value.Should().Be("th");
    }

    [Fact]
    public void TheToolPathAnswersTheSameVerdictAsThePicker()
    {
        var workspace = Workspace();

        workspace.ChooseSourceLanguageCommand.Execute(Row(workspace, "en"));
        workspace.ChooseLanguageCommand.Execute(Row(workspace, "th"));
        SelectModel(workspace, Euro, "EuroLLM");

        var tool = TranslationTools.Inspect(workspace.Direction, Euro, "EuroLLM");

        tool.Status.Should().Be("target_unsupported");
        tool.SourceCode.Should().Be("en");
        tool.TargetCode.Should().Be("th");
        tool.CanTranslate.Should().Be(workspace.DirectionVerdict.CanSend);

        workspace.SwapDirectionCommand.Execute(null);

        TranslationTools.Inspect(workspace.Direction, Euro, "EuroLLM").Status
            .Should().Be("source_unsupported");
    }

    [Fact]
    public void TheToolPathReturnsATypedResultForAnIdenticalPairRatherThanAnError()
    {
        var tool = TranslationTools.Inspect(
            TranslationDirection.Between("en", "English", "en", "English"));

        tool.Status.Should().Be("same_language");
        tool.CanTranslate.Should().BeFalse();
    }

    private static TargetLanguage Row(ChatWorkspaceViewModel workspace, string code) =>
        workspace.Languages.Single(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static void SelectModel(ChatWorkspaceViewModel workspace, string modelId, string modelName)
    {
        workspace.ModelId = modelId;
        workspace.SetModelLanguages(TargetLanguage.ForCatalog(modelId), modelName);
    }

    private ChatWorkspaceViewModel Workspace()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var workspace = new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);

        SelectModel(workspace, Gemma, "TranslateGemma");

        return workspace;
    }
}
