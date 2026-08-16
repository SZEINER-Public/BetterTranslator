using System.IO;
using System.Linq;
using BetterTranslator.Engine.Config;
using BetterTranslator.Engine.Languages;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Editing the engine's configuration from Settings.
///
/// The files ship embedded, which makes them always present and impossible to
/// corrupt but also impossible to edit. A copy on disk takes precedence, and the
/// rule that makes that safe is that nothing invalid is ever written: the engine
/// must never be handed a file it cannot read.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class ConfigEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-config", Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;

    public ConfigEditorTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ConfigStore(_root);
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

    private static ConfigFile Languages => ConfigStore.Find("languages")!;

    private static ConfigFile Prompts => ConfigStore.Find("model-prompts")!;

    [Fact]
    public void ThePromptWordingIsConfigurationRatherThanConstants()
    {
        // It is prompt text: it changes what the model does, so it belongs where
        // someone tuning output can reach it without a compiler.
        var fragments = new BetterTranslator.Engine.Languages.PromptFragments();

        fragments.All.Should().HaveCountGreaterThanOrEqualTo(2);
        fragments.Text(
            BetterTranslator.Engine.Languages.PromptFragments.MemoryHeadingId, "fallback")
            .Should().Contain("not the text to translate",
                "that sentence is what stops the model translating the heading itself");
    }

    [Fact]
    public void AnEmptiedFragmentFallsBackRatherThanLeavingThePromptShort()
    {
        // A file can be edited into something that parses but says nothing. A
        // prompt that silently lost its heading is far harder to notice than one
        // that never had it, so the built-in wording answers instead.
        var fragments = new BetterTranslator.Engine.Languages.PromptFragments(
            [new BetterTranslator.Engine.Languages.PromptFragment { Id = "memoryHeading", Label = "x", Text = "" }]);

        fragments.Text("memoryHeading", BetterTranslator.Engine.Languages.PromptFragments.DefaultMemoryHeading)
            .Should().Be(BetterTranslator.Engine.Languages.PromptFragments.DefaultMemoryHeading);

        fragments.Text("nothing-by-this-name", "fallback").Should().Be("fallback");
    }

    [Fact]
    public void EditedPromptWordingReachesTheActualPrompt()
    {
        var spliced = BetterTranslator.Runtime.Inference.GroundedPrompt.Block("glossary here", "keep names");

        spliced.Should().NotBeNull();
        spliced!.Should().Contain("glossary here").And.Contain("keep names");

        // The wording around them is the configured wording, not a literal.
        spliced.Should().Contain(
            BetterTranslator.Engine.Languages.PromptFragments.Current.Text(
                BetterTranslator.Engine.Languages.PromptFragments.MemoryHeadingId,
                BetterTranslator.Engine.Languages.PromptFragments.DefaultMemoryHeading));
    }

    [Fact]
    public void TheShippedFilesAreAlwaysAvailable()
    {
        foreach (var file in ConfigStore.Files)
        {
            ConfigStore.Shipped(file).Should().NotBeNullOrWhiteSpace($"{file.DisplayName} ships with the engine");
            ConfigStore.Validate(file, ConfigStore.Shipped(file)).Accepted
                .Should().BeTrue($"{file.DisplayName} must be valid as shipped");
        }
    }

    [Fact]
    public void WithNothingSavedTheShippedFileIsWhatLoads()
    {
        _store.IsEdited(Languages).Should().BeFalse();
        _store.Current(Languages).Should().Be(ConfigStore.Shipped(Languages));
    }

    [Fact]
    public void ASavedFileTakesPrecedence()
    {
        var mine = """{"languages":[{"code":"xx","name":"Testish","native":"Testish","dir":"ltr"}]}""";

        _store.Save(Languages, mine).Accepted.Should().BeTrue();
        _store.IsEdited(Languages).Should().BeTrue();
        _store.Current(Languages).Should().Be(mine);

        // And what the registry would load is the edited one.
        new LanguageRegistry(
            BetterTranslator.Engine.Text.BomSafeJson
                .Deserialize<System.Text.Json.JsonElement>(_store.CurrentBytes(Languages))
                .GetProperty("languages")
                .EnumerateArray()
                .Select(e => new LanguageEntry
                {
                    Code = e.GetProperty("code").GetString()!,
                    Name = e.GetProperty("name").GetString()!,
                })
                .ToList())
            .All.Should().ContainSingle(l => l.Code == "xx");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{", null)]
    [InlineData("[]", "top level")]
    [InlineData("""{"nope":[]}""", "languages")]
    [InlineData("""{"languages":[]}""", "empty")]
    public void NothingInvalidIsEverWritten(string candidate, string? expectedInDetail)
    {
        var verdict = _store.Save(Languages, candidate);

        verdict.Accepted.Should().BeFalse();
        verdict.Detail.Should().NotBeNullOrWhiteSpace("a refusal has to say what is wrong");

        if (expectedInDetail is not null)
        {
            verdict.Detail.Should().ContainEquivalentOf(expectedInDetail);
        }

        File.Exists(_store.PathFor(Languages)).Should().BeFalse(
            "a file the engine could not read must never reach the disk");
    }

    [Fact]
    public void AValidFileReportsWhatItActuallyContains()
    {
        var verdict = ConfigStore.Validate(Prompts, ConfigStore.Shipped(Prompts));

        verdict.Accepted.Should().BeTrue();
        verdict.Detail.Should().Contain("1 entry", "the shipped file lists exactly one model");
    }

    [Fact]
    public void ResettingDeletesTheCopyRatherThanOverwritingIt()
    {
        _store.Save(Languages, """{"languages":[{"code":"xx","name":"Testish"}]}""").Accepted.Should().BeTrue();
        File.Exists(_store.PathFor(Languages)).Should().BeTrue();

        _store.Reset(Languages).Accepted.Should().BeTrue();

        File.Exists(_store.PathFor(Languages)).Should().BeFalse("reset removes the copy, it does not rewrite it");
        _store.Current(Languages).Should().Be(ConfigStore.Shipped(Languages));
    }

    [Fact]
    public void ResettingWhenNothingWasSavedIsHarmless()
    {
        var verdict = _store.Reset(Languages);

        verdict.Accepted.Should().BeTrue();
        verdict.Detail.Should().ContainEquivalentOf("already");
    }

    [Fact]
    public void ASavedFileThatLaterBecomesUnreadableFallsBackRatherThanBreakingStartup()
    {
        // Hand-edited outside the app, or truncated by a crash. The application
        // has to start: the editor is where a broken file gets reported, and it
        // cannot be reached if loading it takes the window down.
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.PathFor(Languages), "{ this is not json");

        _store.Current(Languages).Should().Be(ConfigStore.Shipped(Languages));
    }

    [Fact]
    public void SavingWritesNoByteOrderMark()
    {
        // The other half of the rule the engine strips on read. Writing one here
        // would hand the next reader the exact defect BomSafeJson exists for.
        _store.Save(Languages, """{"languages":[{"code":"xx","name":"Testish"}]}""").Accepted.Should().BeTrue();

        var bytes = File.ReadAllBytes(_store.PathFor(Languages));

        bytes.Take(3).Should().NotEqual([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
    }

    [Fact]
    public void ReloadingShowsWhatIsActuallyOnDiskEvenWhenItIsBroken()
    {
        // The distinction ReadRaw exists for. Current() falls back to the
        // shipped text so the application can start; reloading into the editor
        // must not, or an edit broken in another program would look as though it
        // had silently vanished.
        _store.Save(Languages, """{"languages":[{"code":"xx","name":"Testish"}]}""").Accepted.Should().BeTrue();

        File.WriteAllText(_store.PathFor(Languages), """{"languages":[{"code":"xx",""");

        _store.ReadRaw(Languages).Should().StartWith("""{"languages":""")
            .And.NotBe(ConfigStore.Shipped(Languages), "the broken file is what needs fixing, so it is what is shown");

        // Loading, meanwhile, still degrades to something usable.
        _store.Current(Languages).Should().Be(ConfigStore.Shipped(Languages));
    }

    [Fact]
    public void ReadingRawWithNoSavedCopyIsNullRatherThanTheShippedText()
    {
        // Null is "there is no copy", which the caller turns into the shipped
        // text itself. Returning the shipped text here would make "no copy" and
        // "a copy identical to shipped" indistinguishable.
        _store.ReadRaw(Languages).Should().BeNull();
    }

    [Fact]
    public void ABomOnAHandEditedFileIsStillRead()
    {
        // The reverse case: someone saves it from Notepad, which writes one.
        var text = """{"languages":[{"code":"xx","name":"Testish"}]}""";
        File.WriteAllText(_store.PathFor(Languages), text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        _store.Current(Languages).Should().Be(text, "the BOM is stripped rather than making the file unreadable");
    }
}
