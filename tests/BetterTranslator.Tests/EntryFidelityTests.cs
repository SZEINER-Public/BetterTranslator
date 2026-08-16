using System.Globalization;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The audit figures on the entry that holds the pair, which is what Advanced
/// draws under the cost of the run. One cell per figure, so each is asserted
/// as the row renders it rather than as a joined line.
/// </summary>
public sealed class EntryFidelityTests
{
    private static EntryViewModel Entry(string source, string? result = null)
    {
        var view = new EntryViewModel(new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
            TargetLanguage = "Czech",
        });

        view.ShowsFidelity = true;

        if (result is not null)
        {
            view.Complete(result);
        }

        return view;
    }

    [Fact]
    public void Nothing_is_measured_outside_advanced()
    {
        var view = Entry("The build failed on the second stage.", "Sestavení selhalo ve druhé fázi.");

        view.ShowsFidelity = false;

        view.HasFidelity.Should().BeFalse("a binding on a collapsed element still evaluates");
        view.CoverageFigure.Should().BeEmpty();
        view.LineFigure.Should().BeEmpty();
        view.UnitFigure.Should().BeEmpty();
        view.DefectFigure.Should().BeEmpty();
        view.HasDefects.Should().BeFalse();

        view.ShowsFidelity = true;

        view.CoverageFigure.Should().Contain("% translated", "turning Advanced on measures on demand");
    }

    [Fact]
    public void An_entry_with_no_result_has_nothing_to_measure()
    {
        var view = Entry("The build failed.");

        view.HasFidelity.Should().BeFalse();
        view.CoverageFigure.Should().BeEmpty();
        view.DefectFigure.Should().BeEmpty();
    }

    [Fact]
    public void A_translated_entry_reports_its_coverage()
    {
        var view = Entry("The build failed on the second stage.", "Sestavení selhalo ve druhé fázi.");

        view.HasFidelity.Should().BeTrue();
        view.CoverageFigure.Should().Contain("% translated");
    }

    [Fact]
    public void Counts_that_agree_are_not_drawn()
    {
        var view = Entry("The build failed on the second stage.", "Sestavení selhalo ve druhé fázi.");

        // One line and one unit either side. The pair is the expected outcome,
        // and it is the widest thing the row can hold for the least it can say.
        view.LineFigure.Should().BeEmpty();
        view.UnitFigure.Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_went_missing_is_drawn()
    {
        var view = Entry("First line here.\nSecond line here.", "Jen jedna řádka.");

        view.LineFigure.Should().Be("2/1 lines");
    }

    [Fact]
    public void A_span_that_came_back_in_the_source_language_is_counted()
    {
        var view = Entry(
            "The build failed because the cache was cold and nothing was restored.",
            "The build failed because the cache was cold a nic nebylo obnoveno.");

        view.DefectFigure.Should().Contain("span kept");
        view.HasDefects.Should().BeTrue();
    }

    [Fact]
    public void A_clean_pair_says_so_rather_than_leaving_it_out()
    {
        var view = Entry("Sestavení selhalo.", "Sestavení selhalo ve druhé fázi.");

        view.DefectFigure.Should().Be("no defects");
        view.HasDefects.Should().BeFalse("only a finding leaves the grey");
    }

    [Fact]
    public void A_citation_the_output_dropped_is_counted()
    {
        var view = Entry(
            "Open the \"Distilled Rules\" section before writing anything at all.",
            "Otevřete sekci \"Zjednodušená pravidla\" než začnete cokoli psát.");

        view.DefectFigure.Should().Contain("citation lost");
        view.HasDefects.Should().BeTrue();
    }

    [Fact]
    public void Several_findings_are_joined_without_a_dash_run()
    {
        var view = Entry(
            "The build failed because the cache was cold and nothing was restored. Open the \"Distilled Rules\" section.",
            "The build failed because the cache was cold and nothing was restored. Otevřete sekci \"Zjednodušená pravidla\".");

        view.DefectFigure.Should().Contain(", ");
        view.DefectFigure.Should().NotContain(" - ", "the dash run is the device this readout replaced");
    }

    [Fact]
    public void The_measure_is_recomputed_when_the_result_changes()
    {
        var view = Entry("The build failed on the second stage.", "The build failed on the second stage.");

        view.DefectFigure.Should().Contain("span kept");
        view.HasDefects.Should().BeTrue();

        view.Result = "Sestavení selhalo ve druhé fázi.";

        view.DefectFigure.Should().Be("no defects", "the pair changed, so the figures did too");
        view.HasDefects.Should().BeFalse();
    }

    [Fact]
    public void Landing_a_translation_announces_the_figures()
    {
        var view = new EntryViewModel(new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = "The build failed on the second stage.",
            CreatedAt = DateTimeOffset.UtcNow,
            TargetLanguage = "Czech",
        })
        {
            ShowsFidelity = true,
        };

        var announced = new List<string>();

        view.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        view.Complete("Sestavení selhalo ve druhé fázi.");

        // The answer is written before the phase, so the write-back for the text
        // asks these while the phase still says nothing has landed. Without the
        // second announcement the row reads false and stays collapsed for the
        // whole run, which is invisible to any test that reads the property.
        announced.Should().Contain(nameof(EntryViewModel.HasFidelity));
        announced.Should().Contain(nameof(EntryViewModel.CoverageFigure));
        announced.Should().Contain(nameof(EntryViewModel.DefectFigure));
        announced.Should().Contain(nameof(EntryViewModel.HasDefects));

        var landed = announced.LastIndexOf(nameof(EntryViewModel.HasFidelity));
        var settled = announced.IndexOf(nameof(EntryViewModel.HasResultText));

        landed.Should().BeGreaterThan(settled, "the last word on the figures has to come after the phase moved");
        view.HasFidelity.Should().BeTrue();
    }

    [Fact]
    public void A_pair_with_nothing_translatable_states_nothing()
    {
        // Every quoted span, code span and link target is masked before the
        // audit counts, so a JSON resource file leaves no denominator. A
        // percentage over none of them read "0% translated" over work that was
        // done correctly.
        var view = Entry(
            "{\n  \"title\": \"Save\",\n  \"cancel\": \"Cancel\"\n}",
            "{\n  \"title\": \"Uložit\",\n  \"cancel\": \"Zrušit\"\n}");

        view.HasFidelity.Should().BeFalse();
        view.CoverageFigure.Should().BeEmpty();
        view.DefectFigure.Should().BeEmpty();
    }

    [Fact]
    public void The_coverage_percent_is_culture_formatted()
    {
        var view = Entry(
            "The build failed because the cache was cold and nothing was restored.",
            "The build failed because the cache was cold a nic nebylo obnoveno.");

        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

        view.CoverageFigure.Should().EndWith("% translated");
        view.CoverageFigure.Should().NotContain(separator == "." ? "," : ".");
    }
}
