using System.Globalization;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What the run cost, cell by cell. The primary tier of the Advanced readout,
/// so each figure is asserted as its own string: the row draws three of them
/// and any one of them can be the one that is missing.
/// </summary>
public sealed class EntryCostFigureTests
{
    private static EntryViewModel Entry(int? tokens, int? durationMs)
    {
        var view = new EntryViewModel(new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = "The build failed.",
            CreatedAt = DateTimeOffset.UtcNow,
            TargetLanguage = "Czech",
        });

        view.GeneratedTokens = tokens;
        view.DurationMs = durationMs;

        return view;
    }

    [Fact]
    public void An_entry_no_model_ran_for_states_nothing_and_throws_nothing()
    {
        var view = Entry(null, null);

        view.HasMetrics.Should().BeFalse();
        view.TokenFigure.Should().BeEmpty();
        view.DurationFigure.Should().BeEmpty();
        view.RateFigure.Should().BeEmpty();
    }

    [Fact]
    public void A_measured_run_states_all_three()
    {
        var view = Entry(140, 2000);

        view.TokenFigure.Should().Be("140 tokens");
        view.DurationFigure.Should().Be("2 s");
        view.RateFigure.Should().Be("70 tok/s");
    }

    [Fact]
    public void One_token_is_singular()
    {
        var view = Entry(1, 500);

        view.TokenFigure.Should().Be("1 token");
        view.DurationFigure.Should().Be("500 ms");
    }

    [Fact]
    public void A_long_count_is_grouped_by_the_current_culture()
    {
        var view = Entry(4156, 57000);

        var group = CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator;

        view.TokenFigure.Should().Be($"4{group}156 tokens", "a four digit count is one word, not a wrap point");
    }

    [Fact]
    public void A_run_measured_at_zero_states_no_rate()
    {
        var view = Entry(12, 0);

        view.HasMetrics.Should().BeTrue();
        view.DurationFigure.Should().Be("0 ms");
        view.RateFigure.Should().BeEmpty("a rate divided out of no elapsed time is a speed nobody took");
    }

    [Fact]
    public void No_figure_carries_the_separator_the_readout_dropped()
    {
        var view = Entry(4156, 57000);

        view.TokenFigure.Should().NotContain(" - ");
        view.DurationFigure.Should().NotContain(" - ");
        view.RateFigure.Should().NotContain(" - ");
    }
}
