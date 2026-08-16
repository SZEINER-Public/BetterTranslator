using System.IO;
using System.Linq;
using BetterTranslator.Engine.Config;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What a project's own vocabulary is allowed to do to an ordinary sentence.
///
/// The shipped glossary is the reference project's: it requires "store" to become
/// "úložiště". That is right for a model store and wrong for the shop Mr. White
/// went to last night, and applied by default it did damage twice over -- it put
/// the wrong word into the prompt, then refused the correct answer for not using
/// it.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class ProjectVocabularyTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bt-vocab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ResetActiveStore();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The store is static, so a test that points it somewhere has to put it back
    /// or every later test reads a temp folder that no longer exists.
    /// </summary>
    private static void ResetActiveStore() =>
        typeof(ConfigStore)
            .GetProperty(nameof(ConfigStore.Active))!
            .SetValue(null, null);

    private static ConfigFile GlossaryFile =>
        ConfigStore.Find("glossary-cs") ?? throw new InvalidOperationException("no glossary config file");

    private const string Sentence = "Mr. White went to the store last night.";

    private const string Translated = "Pan White šel včera večer do obchodu.";

    [Fact]
    public void ByDefaultNothingIsRequiredOfAnOrdinarySentence()
    {
        ResetActiveStore();
        RagConfig.Forget();

        var rag = RagConfig.For("cs");

        rag.Glossary.IsEmpty.Should().BeTrue();

        Glossary.Hint(rag.Glossary, Sentence, "Czech")
            .Should().BeEmpty("nothing may be pushed into the prompt about a shop");

        Glossary.Check(rag.Glossary, Sentence, Translated)
            .Should().BeNull("and the correct answer may not be refused");
    }

    [Fact]
    public void TheShippedExampleIsStillThereToStartFrom()
    {
        // Removing it would lose the worked example that explains why the three
        // columns differ, which is the part that took measurement to get right.
        var example = Glossary.Shipped("cs");

        example.Terms.Should().NotBeEmpty();
        example.Terms.Should().Contain(t => t.Source == "store");

        ConfigStore.Shipped(GlossaryFile).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AReaderWhoWritesOneGetsItApplied()
    {
        ConfigStore.UseFolder(_folder);
        RagConfig.Forget();

        var saved = ConfigStore.Active!.Save(GlossaryFile, """
            # My glossary

            ## Required terms

            | English | Czech stem (checked) | Shown to the model |
            |---|---|---|
            | store | úložišt | úložiště |
            """);

        saved.Accepted.Should().BeTrue(saved.Detail);
        saved.Detail.Should().Contain("1 required term");

        var rag = RagConfig.For("cs");

        rag.Glossary.Terms.Should().ContainSingle();
        Glossary.Check(rag.Glossary, Sentence, Translated)
            .Should().StartWith("glossary term missing", "the reader asked for this, so it applies");
    }

    [Fact]
    public void AGlossaryIsValidWhenItParsesAndSaysHowMuchItFound()
    {
        // A table typed slightly wrong parses to zero rows and silently does
        // nothing; the count is the only thing that shows it.
        ConfigStore.Validate(GlossaryFile, "# Nothing but prose here").Detail
            .Should().Contain("0 required terms");

        ConfigStore.Validate(GlossaryFile, string.Empty).Accepted.Should().BeFalse();
    }

    [Fact]
    public void ResettingRemovesTheReadersCopyAndTheGlossaryGoesQuietAgain()
    {
        ConfigStore.UseFolder(_folder);
        RagConfig.Forget();

        ConfigStore.Active!.Save(GlossaryFile, """
            ## Required terms

            | English | Czech stem (checked) | Shown to the model |
            |---|---|---|
            | store | úložišt | úložiště |
            """);

        RagConfig.Forget();
        RagConfig.For("cs").Glossary.IsEmpty.Should().BeFalse();

        ConfigStore.Active.Reset(GlossaryFile).Accepted.Should().BeTrue();
        RagConfig.Forget();

        RagConfig.For("cs").Glossary.IsEmpty
            .Should().BeTrue("resetting a file whose shipped copy is an example leaves nothing in force");
    }

    [Fact]
    public void TheOtherConfigFilesStillApplyStraightOutOfTheBox()
    {
        // Only the glossary changed. A registry with no languages would leave the
        // picker empty, which is the opposite of a safe default.
        ResetActiveStore();

        foreach (var file in ConfigStore.Files.Where(f => f.AppliesWhenShipped))
        {
            ConfigStore.BytesFor(file.Id).Should().NotBeEmpty($"{file.DisplayName} is a default, not an example");
        }

        ConfigStore.Files.Count(f => !f.AppliesWhenShipped)
            .Should().Be(1, "the glossary is the only file that describes one project rather than the language");
    }
}
