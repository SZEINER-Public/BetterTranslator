using BetterTranslator.Core.Models;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Models;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What the Advanced panel's two writable settings do to a send.
///
/// The user prompt reaches every prompt on every path. The temperature reaches
/// the request only when the chosen model has no sampler guard: TranslateGemma
/// has one, and it replaces the whole sampler with the model card's greedy
/// settings, the slider included. That is deliberate and it is also invisible in
/// the window, so it is pinned here rather than left to be rediscovered.
/// </summary>
public sealed class AdvancedPanelWiringTests
{
    private const string Guarded = @"C:\models\translategemma-4b-it-Q4_K_M.gguf";
    private const string Unguarded = @"C:\models\eurollm-9b-instruct-Q4_K_M.gguf";

    private static TranslationJob Job(string modelPath, double temperature, string instruction) =>
        TranslationJobs.For(
            new TranslationRequest("The build failed.", Direction(), modelPath),
            new AppSettings { Temperature = temperature, Instruction = instruction });

    private static Core.Languages.TranslationDirection Direction() =>
        Core.Languages.TranslationDirection.Between("en", "English", "cs", "Czech");

    [Fact]
    public void The_user_prompt_is_sent_with_the_text()
    {
        var job = Job(Guarded, 0.2, "Keep product names in English.");

        job.Instruction.Should().Be("Keep product names in English.");

        var built = TranslationPromptBuilder.Build(
            job.ModelPath,
            job.Text,
            "English",
            "en",
            "Czech",
            "cs",
            preamble: GroundedPrompt.Block(job.Memory, job.Instruction),
            templateOverride: ChatTemplate.Gemma);

        built.Should().NotBeNull();
        built!.Text.Should().Contain("Keep product names in English.");

        // Ahead of the translate instruction: anything after that reads as the
        // text to translate.
        built.Text.IndexOf("Keep product names", StringComparison.Ordinal)
            .Should().BeLessThan(built.Text.IndexOf(job.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void An_untouched_user_prompt_adds_nothing_to_the_prompt()
    {
        var job = Job(Guarded, 0.2, string.Empty);

        job.Instruction.Should().BeNull();
        GroundedPrompt.Block(job.Memory, job.Instruction).Should().BeNull();
    }

    [Fact]
    public void The_temperature_is_sent_when_the_model_has_no_sampler_guard()
    {
        Job(Unguarded, 0.7, string.Empty).Sampling().Temperature
            .Should().BeApproximately(0.7f, 0.0001f);

        Job(Unguarded, 0.05, string.Empty).Sampling().Temperature
            .Should().BeApproximately(0.05f, 0.0001f);
    }

    [Fact]
    public void The_temperature_is_replaced_by_the_greedy_settings_on_a_guarded_model()
    {
        var sampling = Job(Guarded, 0.7, string.Empty).Sampling();

        sampling.Temperature.Should().Be(
            0f,
            "the model card's authored example is greedy, and the guard writes that over whatever the slider says");

        sampling.TopK.Should().Be(1);

        // The one place the slider's neighbourhood reappears: a retry loosens the
        // override so a reseeded second attempt can answer differently. It is
        // still not the slider's value.
        Job(Guarded, 0.7, string.Empty).Sampling(attempt: 1).Temperature
            .Should().BeApproximately(0.15f, 0.0001f);
    }
}
