using System.IO;
using BetterTranslator.Engine.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Deciding whether two names refer to the same model.
///
/// Getting it wrong is expensive in both directions: a false negative
/// re-downloads fifteen gigabytes, a false positive loads a different
/// quantisation and translates badly for reasons nothing in the log explains.
/// </summary>
public sealed class ModelIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-models-" + Guid.NewGuid().ToString("N"));

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

    private void GivenModel(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "GGUF");
    }

    [Fact]
    public void APinnedQuantisationIsPartOfTheRequestNotTheName()
    {
        // Left attached, the "-gguf" strip no longer anchors and the token
        // matches nothing -- a model that downloaded perfectly is then reported
        // as not present.
        ModelIdentity.Token("https://huggingface.co/x/translategemma-4b-it-GGUF@Q4_K_M")
            .Should().Be("translategemma-4b-it");

        ModelIdentity.Token("translategemma-4b-it@Q4_K_M").Should().Be("translategemma-4b-it");
    }

    [Fact]
    public void AHubUrlReducesToItsRepositoryName()
    {
        ModelIdentity.Token("https://huggingface.co/google/translategemma-4b-it-GGUF")
            .Should().Be("translategemma-4b-it");

        ModelIdentity.Token("https://huggingface.co/google/translategemma-4b-it-GGUF/?download=1#frag")
            .Should().Be("translategemma-4b-it");
    }

    [Fact]
    public void APlainNameIsLeftAloneApartFromCase()
    {
        // The packaging suffix is stripped only for URLs: a local file genuinely
        // named that way must keep its name.
        ModelIdentity.Token("Google/TranslateGemma-4B-IT").Should().Be("google/translategemma-4b-it");
    }

    [Fact]
    public void AnInsertedSegmentDoesNotMakeItADifferentModel()
    {
        // The recorded failure: google/gemma-4-26b-a4b-qat flattens to
        // "gemma426ba4bqat" while its directory flattens to
        // "gemma426ba4bitqatgguf" -- the inserted "it" breaks the substring.
        ModelIdentity.Matches("gemma-4-26b-a4b-it-qat-GGUF", "gemma-4-26b-a4b-qat").Should().BeTrue();
    }

    [Fact]
    public void SeparatorsAreNotIdentity()
    {
        ModelIdentity.Matches("gemma3-4b-it.gguf", "gemma-3-4b").Should().BeTrue();
        ModelIdentity.Matches("gemma-3-4b-it.gguf", "gemma3-4b").Should().BeTrue();
    }

    [Fact]
    public void ADifferentModelStillDoesNotMatch()
    {
        ModelIdentity.Matches("eurollm-9b-instruct.gguf", "translategemma").Should().BeFalse();
        ModelIdentity.Matches("qwen2.5-7b.gguf", "gemma-3-4b").Should().BeFalse();
        ModelIdentity.Matches("", "translategemma").Should().BeFalse();
        ModelIdentity.Matches("translategemma", "").Should().BeFalse();
        ModelIdentity.Matches(null, null).Should().BeFalse();
    }

    [Fact]
    public void AModelNestedByPublisherIsFound()
    {
        // A real store nests publisher/repo/file, and the identifying words are
        // spread across all three.
        GivenModel("google/translategemma-4b-it-GGUF/translategemma-4b-it-Q4_K_M.gguf");

        ModelIdentity.FindPresent(_root, "google/translategemma-4b-it")
            .Should().NotBeNull().And.EndWith("translategemma-4b-it-Q4_K_M.gguf");

        ModelIdentity.FindPresent(_root, "https://huggingface.co/google/translategemma-4b-it-GGUF@Q4_K_M")
            .Should().NotBeNull();
    }

    [Fact]
    public void AModelThatIsNotThereIsNotInvented()
    {
        GivenModel("google/translategemma-4b-it-GGUF/translategemma-4b-it-Q4_K_M.gguf");

        ModelIdentity.FindPresent(_root, "utter-project/EuroLLM-9B").Should().BeNull();
        ModelIdentity.FindPresent(Path.Combine(_root, "nope"), "translategemma").Should().BeNull();
        ModelIdentity.FindPresent(_root, "").Should().BeNull();
    }

    [Fact]
    public void ATrainedPromptShapeSurvivesTheNameBeingWrittenDifferently()
    {
        // The consequence of getting this wrong is silent: the model loses its
        // trained prompt shape and the only symptom is worse output.
        var prompts = new BetterTranslator.Engine.Languages.ModelPrompts(
        [
            new BetterTranslator.Engine.Languages.ModelPromptEntry
            {
                Match = "gemma-3-4b",
                Label = "Gemma",
                Template = "Translate to {TARGET}",
            },
        ]);

        prompts.For(@"C:\models\google\gemma3-4b-it\gemma3-4b-it-Q4_K_M.gguf", targetLanguage: "Czech")
            .Should().NotBeNull();

        prompts.For(@"C:\models\utter-project\EuroLLM-9B-Q4_K_M.gguf").Should().BeNull();
    }
}
