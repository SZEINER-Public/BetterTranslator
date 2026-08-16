using System.Linq;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Indexing.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// How a Markdown message behaves in the transcript: which of the two ways of
/// reading it is showing, and which messages get the choice at all.
/// </summary>
public sealed class MarkdownMessageTests
{
    private const string Markdown =
        "## Release notes\n\nKeeps your workspace **in sync**.\n\n- Windows 11\n- macOS 14\n";

    private const string Translated =
        "## Poznámky k vydání\n\nUdržuje váš pracovní prostor **synchronizovaný**.\n\n- Windows 11\n- macOS 14\n";

    private static EntryViewModel Entry(string source, string? result = null) =>
        new(new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = source,
            Result = result ?? string.Empty,
            CreatedAt = DateTimeOffset.Now,
            State = result is null ? EntryState.Pending : EntryState.Done,
            TargetLanguage = "Czech",
        });

    [Fact]
    public void A_Markdown_message_opens_rendered()
    {
        var entry = Entry(Markdown, Translated);

        entry.IsMarkdown.Should().BeTrue();
        entry.IsSourceMode.Should().BeFalse("View is the default");

        entry.ShowsSourceMarkdown.Should().BeTrue();
        entry.ShowsResultMarkdown.Should().BeTrue();
        entry.ShowsSourceSyntax.Should().BeFalse();
        entry.ShowsResultSyntax.Should().BeFalse();
        entry.ShowsSourceProse.Should().BeFalse();
        entry.ShowsPlainResult.Should().BeFalse();
    }

    [Fact]
    public void The_switch_moves_both_columns_together()
    {
        var entry = Entry(Markdown, Translated);

        entry.ToggleFormatCommand.Execute(null);

        entry.IsSourceMode.Should().BeTrue();
        entry.ShowsSourceSyntax.Should().BeTrue();
        entry.ShowsResultSyntax.Should().BeTrue();
        entry.ShowsSourceMarkdown.Should().BeFalse();
        entry.ShowsResultMarkdown.Should().BeFalse();

        // Comparing a rendered source against raw translated Markdown compares
        // nothing, which is why there is one switch rather than one per side.
        entry.ToggleFormatCommand.Execute(null);
        entry.ShowsSourceMarkdown.Should().BeTrue();
        entry.ShowsResultMarkdown.Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_message_has_no_second_way_of_being_read()
    {
        var entry = Entry("The build failed on the second stage.", "Sestavení selhalo ve druhé fázi.");

        entry.IsMarkdown.Should().BeFalse("the switch is bound to this and must not appear");
        entry.ShowsSourceProse.Should().BeTrue();
        entry.ShowsPlainResult.Should().BeTrue();
        entry.ShowsSourceMarkdown.Should().BeFalse();
        entry.ShowsResultMarkdown.Should().BeFalse();
        entry.ShowsSourceSyntax.Should().BeFalse();
        entry.ShowsResultSyntax.Should().BeFalse();
    }

    /// <summary>
    /// Source mode and the Copy button read the same property. Copy has always
    /// put <see cref="EntryViewModel.Result"/> on the clipboard and Source mode
    /// binds the same string, so the two agree by construction -- provided the
    /// entry never rewrites what it was given, which is what this holds.
    /// </summary>
    [Fact]
    public void The_message_is_stored_as_written_so_Source_and_Copy_are_the_raw_Markdown()
    {
        var entry = Entry(Markdown, Translated);

        entry.Source.Should().Be(Markdown);
        entry.Result.Should().Be(Translated);
    }

    [Fact]
    public void Rendering_follows_the_translation_rather_than_the_source()
    {
        var entry = Entry(Markdown, Translated);

        Headings(entry.SourceBlocks).Should().Equal("Release notes");
        Headings(entry.ResultBlocks).Should().Equal("Poznámky k vydání");
    }

    [Fact]
    public void Changing_the_result_reparses_it()
    {
        var entry = Entry(Markdown, Translated);

        Headings(entry.ResultBlocks).Should().Equal("Poznámky k vydání");

        entry.Result = "## Later\n\nA different document.\n";

        Headings(entry.ResultBlocks).Should().Equal("Later");
    }

    [Fact]
    public void A_Markdown_message_still_waiting_shows_neither_view_of_a_result()
    {
        var entry = Entry(Markdown);

        entry.IsMarkdown.Should().BeTrue("the switch belongs to the message, not to the answer");
        entry.HasResultText.Should().BeFalse();
        entry.ShowsResultMarkdown.Should().BeFalse();
        entry.ShowsResultSyntax.Should().BeFalse();

        // And the sent message renders while the answer is still out.
        entry.ShowsSourceMarkdown.Should().BeTrue();
    }

    [Fact]
    public void A_message_whose_source_is_Markdown_stays_Markdown_if_the_answer_comes_back_flat()
    {
        // A model that returned prose for a Markdown send has produced a bad
        // answer, not a different kind of message. Deciding from the result
        // would let one bad translation take the switch away.
        var entry = Entry(Markdown, "Poznámky k vydání");

        entry.IsMarkdown.Should().BeTrue();
        entry.ShowsResultMarkdown.Should().BeTrue();
    }

    private static IEnumerable<string> Headings(IReadOnlyList<MdBlock> blocks) =>
        blocks.OfType<MdHeading>().Select(h => string.Concat(h.Inlines.Select(i => i.Text)));
}
