using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What happens when the model does not do as it was asked.
///
/// It will not, sometimes. The point of the sentinel check is that a model
/// dropping a bracket costs a worse translation of one block and never a broken
/// document, so every case here asserts the same invariant: whatever came back,
/// the markup is exactly where it was.
///
/// The string the model is shown for <see cref="Doc"/> is
/// `See the [[0]]changelog[[1]] for [[2]]all[[3]] the details.` -- every bracket
/// and asterisk lifted out, the sentence still whole.
/// </summary>
public sealed class MarkdownFallbackTests
{
    private const string Doc = "See the [changelog](https://example.com/log) for **all** the details.\n";

    /// <summary>A character no answer contains, for swapping two sentinels.</summary>
    private const string Swap = "";

    private static Func<string, CancellationToken, Task<string?>> Model(Func<string, string?> answer) =>
        (text, _) => Task.FromResult(answer(text));

    /// <summary>
    /// Marks what the model touched. Guillemets cannot occur anywhere else, so
    /// stripping them has to give the source back -- on the fallback path just
    /// as much as on the normal one.
    /// </summary>
    private static string Mark(string text) => "«" + text + "»";

    private static string Unmark(string text) =>
        text.Replace("«", string.Empty).Replace("»", string.Empty);

    [Fact]
    public async Task A_dropped_sentinel_falls_back_and_keeps_the_link_whole()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Doc,
            Model(text => Mark(text).Replace("[[1]]", string.Empty)));

        result.Recovered.Should().Be(1, "the block was retranslated run by run");
        result.Text.Should().Contain("](https://example.com/log)");
        result.Text.Should().Contain("**");
        Unmark(result.Text).Should().Be(Doc);
    }

    [Fact]
    public async Task A_reordered_pair_falls_back_rather_than_wrapping_the_wrong_words()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Doc,
            // Both sentinels are still present; only their order is wrong, which
            // would close the link before it opened.
            Model(text => Mark(text)
                .Replace("[[0]]", Swap)
                .Replace("[[1]]", "[[0]]")
                .Replace(Swap, "[[1]]")));

        result.Recovered.Should().Be(1);
        result.Text.Should().Contain("[").And.Contain("](https://example.com/log)");
        Unmark(result.Text).Should().Be(Doc);
    }

    [Fact]
    public async Task An_invented_sentinel_never_reaches_the_reader()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            Doc,
            Model(text => Mark(text) + " [[9]]"));

        // Refused at block level for the count, then again on every run, because
        // a run was never given a sentinel to carry. Nothing is usable, so the
        // block keeps its source -- which is the outcome that matters.
        result.Kept.Should().Be(1);
        result.Text.Should().Be(Doc);
        result.Text.Should().NotContain("[[", "a sentinel is machinery and must never be shown");
    }

    [Fact]
    public async Task A_blank_line_in_an_answer_is_refused_because_it_would_split_the_block()
    {
        const string List = "- One item that stays one item\n- And another\n";

        var result = await MarkdownTranslation.TranslateAsync(
            List,
            Model(text => text + "\n\nand a second paragraph"));

        result.Text.Should().Be(List);
        result.Kept.Should().Be(2, "neither item's block nor its run gave a usable answer");
    }

    [Fact]
    public async Task Nothing_coming_back_leaves_the_document_exactly_as_it_was()
    {
        var result = await MarkdownTranslation.TranslateAsync(Doc, Model(_ => null));

        result.Text.Should().Be(Doc);
        result.Kept.Should().Be(1);
        result.StructureHeld.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_answer_carrying_a_line_break_is_refused()
    {
        // On the fallback path a run sits inside a line. An answer with a break
        // in it would push the rest of the sentence onto a new line, and inside
        // a list that is a new item.
        var result = await MarkdownTranslation.TranslateAsync(
            Doc,
            Model(text => text.Contains("[[") ? "dropped every sentinel" : "one\ntwo"));

        result.Text.Should().Be(Doc);
        result.Kept.Should().Be(1);
    }

    /// <summary>
    /// Fourteen links in one paragraph is twenty-eight sentinels. The measured
    /// limit recorded in MarkupGuard is that a small model cannot echo that many
    /// in order, so spending the call to find out is waste.
    /// </summary>
    [Fact]
    public async Task A_block_too_dense_with_markup_goes_run_by_run_without_being_sent_whole()
    {
        var dense = string.Concat(Enumerable.Range(0, 14).Select(i => $"[link {i}](https://example.com/{i}) "));

        MarkdownSegmenter.Segment(dense)[0].Guards.Should().HaveCountGreaterThan(12);

        var result = await MarkdownTranslation.TranslateAsync(dense, Model(Mark));

        result.Translated.Should().Be(0, "it was never sent whole");
        result.Recovered.Should().Be(1);

        for (var i = 0; i < 14; i++)
        {
            result.Text.Should().Contain($"](https://example.com/{i})");
        }

        Unmark(result.Text).Should().Be(dense);
    }
}
