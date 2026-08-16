using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The Markdown translation path, held to the one rule it exists for: the
/// document that comes out is the document that went in, with only its prose
/// changed.
///
/// The fixture is deliberately one document rather than a case per construct.
/// The failures worth catching are interactions -- a heading that eats the list
/// under it, a fence whose language tag gets translated, a table whose pipes
/// move -- and none of those appear when each construct is parsed alone.
/// </summary>
public sealed class MarkdownRoundTripTests
{
    private const string Fixture =
        """
        ---
        title: Release notes
        version: 2.4
        ---

        # Bubble Desktop 2.4

        One folder, every machine. Bubble keeps your workspace **in sync** while
        you *work*, and the [changelog](https://bubble.example.com/changelog) has
        the rest.

        ## What changed

        - Fixed a locked file stalling the queue
        - Rewrote the watcher on `FileSystemWatcher`
          - Windows 11 only
          - macOS 14 lands next month

        ### Checklist

        - [x] Ship the watcher
        - [ ] Document the retry budget

        > Upgrading from 2.3 needs no migration. Your settings carry over.

        | Setting | Default | Notes |
        | --- | --- | --- |
        | Watch | on | Follows the folder |
        | Retry | 3 | Backs off each time |

        Run it with a flag:

        ```bash
        bubble sync --watch --retry 3
        ```

        ![The sync indicator](https://bubble.example.com/sync.png)

        See <https://bubble.example.com/docs> for the full reference.
        """;

    /// <summary>
    /// Nothing is sent anywhere: every unit's protected text has its markup put
    /// straight back and is spliced where it came from. If segmentation loses a
    /// character, this is where it shows, with no model in the way to blame.
    /// </summary>
    [Fact]
    public void Segmentation_and_reassembly_are_lossless()
    {
        var units = MarkdownSegmenter.Segment(Fixture);
        var text = Fixture;

        foreach (var unit in units.OrderByDescending(u => u.Start))
        {
            var restored = MarkdownEcho.Restore(unit.Text, unit.Guards);
            text = text.Remove(unit.Start, unit.Length).Insert(unit.Start, restored);
        }

        text.Should().Be(Fixture);
    }

    /// <summary>The same document through the whole path, with a model that echoes.</summary>
    [Fact]
    public async Task A_document_nothing_changes_comes_back_byte_identical()
    {
        var result = await MarkdownTranslation.TranslateAsync(Fixture, (text, _) => Task.FromResult<string?>(text));

        result.Text.Should().Be(Fixture);
        result.StructureHeld.Should().BeTrue();
        result.Kept.Should().Be(0, "an echoed sentinel is a valid answer, not a failure");
    }

    /// <summary>
    /// The structural diff, as an exact statement rather than an eyeball.
    ///
    /// The model wraps everything it is given in guillemets. Those characters
    /// can only appear where translated text was spliced in, so stripping them
    /// from the output has to give back the source exactly. Anything the model
    /// was wrongly allowed to touch would leave a mark that stripping cannot
    /// remove -- a moved pipe, a translated fence tag, a rewritten URL.
    /// </summary>
    [Fact]
    public async Task Only_prose_changes_and_everything_else_is_untouched()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Fixture,
            (text, _) => Task.FromResult<string?>("«" + text + "»"));

        result.StructureHeld.Should().BeTrue();
        result.Text.Should().NotBe(Fixture, "the prose was translated");

        result.Text.Replace("«", "").Replace("»", "")
            .Should().Be(Fixture, "nothing outside a prose span may have moved");
    }

    [Theory]
    // The fence, its language tag and the code inside it.
    [InlineData("```bash\nbubble sync --watch --retry 3\n```")]
    // Link and image targets, and an autolink.
    [InlineData("(https://bubble.example.com/changelog)")]
    [InlineData("(https://bubble.example.com/sync.png)")]
    [InlineData("<https://bubble.example.com/docs>")]
    // An inline code span carries no prose.
    [InlineData("`FileSystemWatcher`")]
    // Front matter.
    [InlineData("---\ntitle: Release notes\nversion: 2.4\n---")]
    // Table pipes and the delimiter row.
    [InlineData("| --- | --- | --- |")]
    // Task list markers.
    [InlineData("- [x] ")]
    [InlineData("- [ ] ")]
    public async Task What_must_survive_survives_verbatim(string fragment)
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Fixture,
            (text, _) => Task.FromResult<string?>("«" + text + "»"));

        result.Text.Should().Contain(fragment);
    }

    [Theory]
    [InlineData("changelog", "link text is prose and is translated")]
    [InlineData("The sync indicator", "image alt text is prose and is translated")]
    [InlineData("Bubble Desktop 2.4", "a heading is prose")]
    [InlineData("Follows the folder", "a table cell is prose")]
    [InlineData("Ship the watcher", "a task list item is prose")]
    [InlineData("Upgrading from 2.3 needs no migration.", "a blockquote is prose")]
    [InlineData("Windows 11 only", "a nested list item is prose")]
    public async Task What_must_be_translated_is_translated(string prose, string because)
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Fixture,
            (text, _) => Task.FromResult<string?>("«" + text + "»"));

        // Wrapped means it went through the model; the exact wrapping depends on
        // where the sentinels fell, so the assertion is that it moved at all.
        result.Text.Should().NotContain("\n" + prose, because);
        result.Text.Should().Contain(prose, because + " -- and translation must not lose it");
    }

    /// <summary>
    /// A sentence that runs through bold, a link and plain text is one call, not
    /// four. Four calls means four separately guessed grammars for one sentence.
    /// </summary>
    [Fact]
    public void An_inline_marked_sentence_is_one_unit()
    {
        const string Sentence = "Bubble keeps your workspace **in sync** while you *work* today.";

        var units = MarkdownSegmenter.Segment(Sentence);

        units.Should().ContainSingle();
        units[0].Runs.Should().HaveCountGreaterThan(1, "the sentence really is split by markup");
        units[0].Text.Should().Be("Bubble keeps your workspace [[0]]in sync[[1]] while you [[2]]work[[3]] today.");
    }

    [Fact]
    public void A_soft_line_break_stays_prose_rather_than_becoming_a_sentinel()
    {
        var units = MarkdownSegmenter.Segment("The build failed on the second stage\nbecause the cache was cold.");

        units.Should().ContainSingle();
        units[0].Guards.Should().BeEmpty("a line break inside a paragraph is whitespace, not markup");
        units[0].Text.Should().Contain("\n");
    }
}
