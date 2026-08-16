using System.IO;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The panel says when the temperature it holds is not the one that went out,
/// and the slider stops claiming otherwise.
///
/// Neither happened. The guard rewrites the whole sampler for a model whose card
/// publishes a greedy example, and the advisory that exists to report that was
/// handed the rewritten values, so it compared the guard's output against the
/// guard's own rules and always passed. Meanwhile the slider sat live showing the
/// reader's figure while zero was sent.
/// </summary>
public sealed class SamplerAdvisoryTests
{
    [Fact]
    public void An_overridden_temperature_is_reported_with_both_figures()
    {
        var notice = SamplerAdvisory.Describe("TranslateGemma", requested: 0.7, sent: 0);

        notice.Should().NotBeNull();
        notice.Should().Contain("0.70").And.Contain("0.00").And.Contain("TranslateGemma");
    }

    [Fact]
    public void A_temperature_that_survived_is_not_reported()
    {
        SamplerAdvisory.Describe("EuroLLM", requested: 0.7, sent: 0.7)
            .Should().BeNull("there is nothing to say when the request went out as it was");
    }

    [Fact]
    public void A_difference_the_slider_cannot_express_is_not_a_difference()
    {
        // The slider steps in 0.05, so anything under half a step is the same
        // value arriving through a float.
        SamplerAdvisory.Describe("EuroLLM", requested: 0.2, sent: 0.2f)
            .Should().BeNull();
    }

    [Fact]
    public void The_advisory_matches_what_the_job_will_actually_send()
    {
        var settings = new AppSettings { Temperature = 0.7 };

        var guarded = TranslationJobs.For(
            new TranslationRequest("x", Direction(), @"C:\models\translategemma-4b-it-Q4_K_M.gguf"),
            settings);

        SamplerAdvisory.Describe("TranslateGemma", guarded.Temperature, guarded.Sampling().Temperature)
            .Should().NotBeNull("this is the case the reader needs told");

        var open = TranslationJobs.For(
            new TranslationRequest("x", Direction(), @"C:\models\eurollm-9b-instruct-Q4_K_M.gguf"),
            settings);

        SamplerAdvisory.Describe("EuroLLM", open.Temperature, open.Sampling().Temperature)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(TranslationEffort.Fast, true)]
    [InlineData(TranslationEffort.Thinking, false)]
    public void The_slider_is_live_only_for_a_model_that_reads_it(TranslationEffort effort, bool adjustable)
    {
        var root = Path.Combine(Path.GetTempPath(), "bt-advisory", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var paths = new InstallPaths(new AppPaths(root));

            paths.EnsureCreated();

            var view = new TranslationSettingsViewModel(paths, () => { });

            // Set rather than restored: Restore only lands on a model that is on
            // disk, and nothing is installed here.
            view.SelectedEffort = view.Efforts.Single(option => option.Effort == effort);

            view.TemperatureIsAdjustable.Should().Be(adjustable);

            if (adjustable)
            {
                view.TemperatureNote.Should().NotContain("greedy");
            }
            else
            {
                view.TemperatureNote.Should().Contain("greedy", "a locked control has to say why it is locked");
                view.TemperatureNote.Should().Contain(view.Model.Name);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Core.Languages.TranslationDirection Direction() =>
        Core.Languages.TranslationDirection.Between("en", "English", "cs", "Czech");
}
