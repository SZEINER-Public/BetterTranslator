using BetterTranslator.Indexing.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The block mapping the preview binds to. Each block type has its own
/// DataTemplate, so a block that maps to the wrong type renders as the wrong
/// thing rather than not at all.
/// </summary>
public sealed class MarkdownBlockParserTests
{
    [Fact]
    public void HeadingsKeepTheirLevel()
    {
        var blocks = MarkdownBlockParser.Parse("# One\n\n## Two\n\n### Three");

        blocks.OfType<MdHeading>().Select(h => h.Level).Should().Equal(1, 2, 3);
        blocks.OfType<MdHeading>().First().Inlines.Single().Text.Should().Be("One");
    }

    [Fact]
    public void BulletItemsBecomeOneEntryEach()
    {
        var blocks = MarkdownBlockParser.Parse("- Windows 11 or macOS 14\n- 500 MB free disk space");

        var list = blocks.OfType<MdBulletList>().Single();
        list.Ordered.Should().BeFalse();
        list.Items.Should().HaveCount(2);
        list.Items[0].Inlines.Single().Text.Should().Be("Windows 11 or macOS 14");
        list.Items[0].Marker.Should().Be("•");
        list.Items[0].Done.Should().BeNull("a plain item is not a task");
    }

    [Fact]
    public void OrderedListsCountRatherThanBullet()
    {
        var list = MarkdownBlockParser.Parse("1. First\n2. Second").OfType<MdBulletList>().Single();

        list.Ordered.Should().BeTrue();
        list.Items.Select(i => i.Marker).Should().Equal("1.", "2.");
    }

    [Fact]
    public void OrderedListsStartWhereTheSourceSaysTheyDo() =>
        MarkdownBlockParser.Parse("7. Seventh\n8. Eighth")
            .OfType<MdBulletList>().Single()
            .Items.Select(i => i.Marker).Should().Equal("7.", "8.");

    [Fact]
    public void ANestedListIsKeptUnderItsItemRatherThanFlattenedAway()
    {
        var list = MarkdownBlockParser.Parse("- Rewrote the watcher\n  - Windows 11 only\n  - macOS 14 next")
            .OfType<MdBulletList>().Single();

        list.Items.Should().ContainSingle();
        list.Items[0].Inlines.Single().Text.Should().Be("Rewrote the watcher");

        var nested = list.Items[0].Children.OfType<MdBulletList>().Single();
        nested.Items.Select(i => i.Inlines.Single().Text)
            .Should().Equal("Windows 11 only", "macOS 14 next");
    }

    [Fact]
    public void TaskItemsCarryTheirBoxRatherThanTheCharactersThatDrewIt()
    {
        var list = MarkdownBlockParser.Parse("- [x] Ship the watcher\n- [ ] Document the budget")
            .OfType<MdBulletList>().Single();

        list.Items.Select(i => i.Done).Should().Equal(true, false);
        list.Items[0].Inlines.Single().Text.Should().Be("Ship the watcher",
            "the [x] is a box to draw, not text to show");
    }

    [Fact]
    public void ALinkCarriesItsTargetSoTheRendererCanFollowIt()
    {
        var inlines = MarkdownBlockParser.Parse("See the [changelog](https://example.com/log) for more.")
            .OfType<MdParagraph>().Single().Inlines;

        inlines.Should().Contain(i => i.Text == "changelog" && i.Link == "https://example.com/log");
        inlines.Should().Contain(i => i.Text == "See the " && i.Link == null);
    }

    [Fact]
    public void AnImageShowsItsAltTextAndOffersNoLinkToFollow()
    {
        var inlines = MarkdownBlockParser.Parse("![The sync indicator](https://example.com/sync.png)")
            .OfType<MdParagraph>().Single().Inlines;

        inlines.Single().Text.Should().Be("The sync indicator");
        inlines.Single().Link.Should().BeNull();
    }

    [Fact]
    public void FencedCodeKeepsItsTextAndLanguage()
    {
        var blocks = MarkdownBlockParser.Parse("```bash\nbubble sync --watch\n```");

        var code = blocks.OfType<MdCodeBlock>().Single();
        code.Language.Should().Be("bash");
        code.Text.Should().Be("bubble sync --watch");
    }

    [Fact]
    public void IndentedCodeIsAlsoACodeBlock() =>
        MarkdownBlockParser.Parse("    bubble sync --retry")
            .OfType<MdCodeBlock>().Single().Text.Should().Contain("bubble sync --retry");

    [Fact]
    public void PipeTablesParseIntoRows()
    {
        // Requires UseAdvancedExtensions on the pipeline.
        var blocks = MarkdownBlockParser.Parse(
            "| Setting | Default |\n| --- | --- |\n| Watch | on |\n| Retry | 3 |");

        var table = blocks.OfType<MdTable>().Single();
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("Setting", "Default");
        table.Rows[2].Should().Equal("Retry", "3");
    }

    [Fact]
    public void BoldAndItalicAreDistinguished()
    {
        var inlines = MarkdownBlockParser.Parse("Keeps **in sync** while you *work*.")
            .OfType<MdParagraph>().Single().Inlines;

        inlines.Should().Contain(i => i.Text == "in sync" && i.Bold && !i.Italic);
        inlines.Should().Contain(i => i.Text == "work" && i.Italic && !i.Bold);
    }

    [Fact]
    public void InlineCodeIsMarkedAsCode() =>
        MarkdownBlockParser.Parse("Run `bubble sync` first.")
            .OfType<MdParagraph>().Single().Inlines
            .Should().Contain(i => i.Text == "bubble sync" && i.Code);

    [Fact]
    public void AdjacentRunsWithTheSameMarksAreMerged()
    {
        // Otherwise a paragraph becomes one inline per literal fragment.
        var inlines = MarkdownBlockParser.Parse("plain text with no marks at all")
            .OfType<MdParagraph>().Single().Inlines;

        inlines.Should().ContainSingle();
    }

    [Fact]
    public void EmptyInputProducesNoBlocks() =>
        MarkdownBlockParser.Parse(string.Empty).Should().BeEmpty();
}
