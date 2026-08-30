using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What makes a retry a second attempt rather than a repeat.
///
/// The pipeline sends once, runs every gate, and on a refusal tries again. That
/// is only worth the seconds if the second request can produce a different
/// answer -- and the runtime seeds deterministically, so an identical request
/// returns identical bytes. Measured across three models and three temperatures
/// including 0.7: two runs of the same job were byte-identical every time.
/// </summary>
public sealed class SamplingRetryTests
{
    private static TranslationJob Job => new()
    {
        Text = "I appreciate the opportunity I've had here.",
        ModelPath = @"C:\models\whatever.gguf",
        Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
    };

    [Fact]
    public void TheSecondAttemptAsksADifferentQuestionOfTheSampler()
    {
        var first = Job.Sampling(0);
        var second = Job.Sampling(1);

        second.Seed.Should().NotBe(first.Seed, "otherwise the retry cannot change the answer");
    }

    [Fact]
    public void NothingElseAboutSamplingMovesWithTheAttempt()
    {
        // A retry is a re-roll, not a different request. Changing the temperature
        // or the budget as well would make the second answer incomparable with the
        // first, and a gate that refused the first for being over-long would then
        // be judging a different thing.
        var first = Job.Sampling(0);
        var second = Job.Sampling(1);

        second.Temperature.Should().Be(first.Temperature);
        second.MaxTokens.Should().Be(first.MaxTokens);
        second.TopP.Should().Be(first.TopP);
        second.TopK.Should().Be(first.TopK);
        second.RepeatPenalty.Should().Be(first.RepeatPenalty);
    }

    [Fact]
    public void TheSameAttemptIsAlwaysTheSameSeed()
    {
        // Derived from the seed in force rather than from the clock, so a defect
        // found on attempt two can be reproduced rather than only witnessed.
        Job.Sampling(1).Seed.Should().Be(Job.Sampling(1).Seed);
        Job.Sampling(0).Seed.Should().Be(Job.Sampling().Seed);
    }

    [Fact]
    public void EachAttemptGetsItsOwnSeed()
    {
        var seeds = new[] { 0, 1, 2, 3 }.Select(a => Job.Sampling(a).Seed).ToList();

        seeds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EffortStillDecidesTheBudgetAndNothingElse()
    {
        var fast = (Job with { Effort = TranslationEffort.Simple }).Sampling();
        var thinking = (Job with { Effort = TranslationEffort.Thinking }).Sampling();

        thinking.MaxTokens.Should().BeGreaterThan(fast.MaxTokens);
        thinking.Temperature.Should().Be(fast.Temperature);
        thinking.Seed.Should().Be(fast.Seed);
    }
}
