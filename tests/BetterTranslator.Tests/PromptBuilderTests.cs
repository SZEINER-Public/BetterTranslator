using System.IO;
using System.Linq;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Building the prompt from what the model itself declares.
///
/// The runtime builds exactly one prompt -- TranslateGemma's instruction inside
/// Gemma markers -- for every model it loads. That is right for TranslateGemma
/// and wrong twice over for anything else: wrong instruction, wrong turn
/// markers. Both facts needed to do better are available, one in the model's
/// GGUF header and one in model-prompts.json.
/// </summary>
public sealed class PromptBuilderTests(ITestOutputHelper output)
{
    private const string GemmaTemplate =
        "{% for message in messages %}{{ '<start_of_turn>' + role + '\\n' + message['content'] "
        + "| trim + '<end_of_turn>\\n' }}{% endfor %}{{ '<start_of_turn>model\\n' }}";

    private const string ChatMlTemplate =
        "{% for message in messages %}<|im_start|>{{ role }}\\n{{ message['content'] | trim }}"
        + "<|im_end|>\\n{% endfor %}{{'<|im_start|>assistant\\n'}}";

    [Fact]
    public void TheTwoShapesAreRecognisedByTheirMarkers()
    {
        ChatTemplate.Detect(GemmaTemplate).Should().Be(ChatTemplate.Gemma);
        ChatTemplate.Detect(ChatMlTemplate).Should().Be(ChatTemplate.ChatMl);
    }

    [Fact]
    public void AnUnknownShapeIsDeclinedRatherThanGuessedAt()
    {
        // The caller falls back to the runtime's prompt. Rendering a template
        // this does not understand would produce markers the model has never
        // seen, which is worse than the wrong-but-valid prompt it had before.
        ChatTemplate.Detect("[INST] {{ content }} [/INST]").Should().BeNull();
        ChatTemplate.Detect("").Should().BeNull();
        ChatTemplate.Detect(null).Should().BeNull();
    }

    [Fact]
    public void GemmaFoldsASystemMessageIntoTheUserTurn()
    {
        // Gemma has no system role: its template routes user and system through
        // the same branch, so a system turn renders as a SECOND user turn -- a
        // stray message before the real one.
        var rendered = ChatTemplate.Gemma.Render(
            [new ChatMessage("system", "RULES"), new ChatMessage("user", "TEXT")]);

        output.WriteLine(rendered.Replace("\n", "\\n"));

        rendered.Should().Be("<start_of_turn>user\nRULES\n\nTEXT<end_of_turn>\n<start_of_turn>model\n");
        rendered.Split("<start_of_turn>").Should().HaveCount(3, "one user turn and the generation prompt");
    }

    [Fact]
    public void ChatMlKeepsASystemMessageAsItsOwnTurn()
    {
        var rendered = ChatTemplate.ChatMl.Render(
            [new ChatMessage("system", "RULES"), new ChatMessage("user", "TEXT")]);

        output.WriteLine(rendered.Replace("\n", "\\n"));

        rendered.Should().Be(
            "<|im_start|>system\nRULES<|im_end|>\n<|im_start|>user\nTEXT<|im_end|>\n<|im_start|>assistant\n");
    }

    [Fact]
    public void ATrainedModelGetsItsOwnInstructionInItsOwnMarkers()
    {
        var built = TranslationPromptBuilder.Build(
            "translategemma-4b-it.Q4_K_M.gguf", "The build failed.", "English", "en", "Czech", "cs",
            templateOverride: ChatTemplate.Gemma);

        built.Should().NotBeNull();
        output.WriteLine(built!.Text.Replace("\n", "\\n"));

        built.TemplateName.Should().Be("Gemma");
        built.InstructionSource.Should().Be("TranslateGemma");

        // The model card's own wording, and its two blank lines before the text.
        built.Text.Should().Contain("You are a professional English (en) to Czech (cs) translator");
        built.Text.Should().Contain("into Czech:\n\n\nThe build failed.");
        built.Text.Should().EndWith("<start_of_turn>model\n");
    }

    [Fact]
    public void AModelWithNoPublishedShapeGetsTheEnginePromptInItsOwnMarkers()
    {
        var built = TranslationPromptBuilder.Build(
            "EuroLLM-9B-Instruct-Q4_K_M.gguf", "The build failed.", "English", "en", "Czech", "cs",
            templateOverride: ChatTemplate.ChatMl);

        built.Should().NotBeNull();
        output.WriteLine(built!.Text.Replace("\n", "\\n"));

        built.TemplateName.Should().Be("ChatML");
        built.InstructionSource.Should().Be("engine");

        // Not TranslateGemma's, which is what it used to be handed.
        built.Text.Should().NotContain("You are a professional English (en) to Czech (cs) translator");
        built.Text.Should().Contain("You are a professional translator. Translate the user's text into Czech.");

        // The clause that makes the markup guard's sentinels mean anything.
        built.Text.Should().Contain("[[7]] are protected blocks");

        built.Text.Should().StartWith("<|im_start|>system\n").And.EndWith("<|im_start|>assistant\n");
    }

    [Fact]
    public void RetrievedMemoryGoesAheadOfTheInstruction()
    {
        // Anything after "translate the following text" reads as the text to
        // translate. Measured: placed after, the model translated the block.
        var built = TranslationPromptBuilder.Build(
            "translategemma-4b-it.Q4_K_M.gguf", "The build failed.", "English", "en", "Czech", "cs",
            preamble: "GLOSSARY BLOCK",
            templateOverride: ChatTemplate.Gemma);

        built.Should().NotBeNull();

        built!.Text.IndexOf("GLOSSARY BLOCK", StringComparison.Ordinal)
            .Should().BeLessThan(built.Text.IndexOf("You are a professional", StringComparison.Ordinal));
    }

    [Fact]
    public void NoTemplateMeansNoPromptRatherThanAnInventedOne()
    {
        TranslationPromptBuilder.Build(
            "whatever.gguf", "text", "English", "en", "Czech", "cs",
            templateOverride: null)
            .Should().BeNull("a file that declares nothing this can render falls back to the runtime");
    }

    [ModelFact]
    public void TheRealModelsOnThisMachineDeclareShapesWeCanRender()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio-shared", "models");

        var checkedAny = false;

        foreach (var (pattern, expected) in new[]
        {
            ("EuroLLM-9B-Instruct-Q4_K_M.gguf", "ChatML"),
            ("translategemma-4b-it.Q4_K_M.gguf", "Gemma"),
        })
        {
            var path = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();

            if (path is null)
            {
                continue;
            }

            checkedAny = true;

            var template = ChatTemplate.For(path);
            output.WriteLine($"{pattern}: {template?.Name ?? "<unknown>"}");

            template.Should().NotBeNull($"{pattern} declares a template in its GGUF header");
            template!.Name.Should().Be(expected);
        }

        checkedAny.Should().BeTrue("this gate exists because the models are on this machine");
    }
}
