using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The one defect a reader cannot find by comparing two columns: a document that
/// came back short. Every other structural count held on a file that lost thirty
/// nine lines, so the line count is its own backstop.
/// </summary>
public sealed class LineParityTests
{
    private static string English =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationAudit", "claude-en.md"));

    [TranslationAuditFact]
    public void A_missing_line_is_reported_and_an_intact_document_is_not()
    {
        var shortened = string.Join('\n', English.Replace("\r\n", "\n").Split('\n').Skip(1));

        LineParity.Compare(English, shortened).Should().NotBeNull("a line went missing");
        LineParity.Compare(English, English).Should().BeNull("nothing moved");
    }

    [Fact]
    public void The_count_is_taken_after_line_endings_are_normalised()
    {
        LineParity.Count("a\r\nb\r\nc").Should().Be(3);
        LineParity.Count("a\nb\nc").Should().Be(3);
        LineParity.Compare("a\r\nb", "a\nb").Should().BeNull();
    }

    [Fact]
    public async Task An_answer_that_welds_two_lines_is_rejected_before_it_is_spliced()
    {
        const string Markdown =
            "# Title\n\nFirst line of one paragraph\nsecond line of the same paragraph.\n";

        var welded = await MarkdownTranslation.TranslateAsync(
            Markdown,
            (text, _) => Task.FromResult<string?>(text.Replace("\n", " ")));

        welded.StructureHeld.Should().BeTrue("the weld never reaches the document");
        welded.Kept.Should().BeGreaterThan(0, "the unit fell to the run path, which had nothing usable either");
        LineParity.Compare(Markdown, welded.Text).Should().BeNull("every line is still there");
    }

    [Fact]
    public async Task A_line_that_goes_missing_after_the_splice_refuses_the_document()
    {
        const string Markdown = "# Title\n\nOne paragraph on one line.\n";

        var dropped = await MarkdownTranslation.TranslateAsync(
            Markdown,
            (text, _) => Task.FromResult<string?>(text.Contains("paragraph", StringComparison.Ordinal)
                ? "Jeden odstavec."
                : text));

        LineParity.Compare(Markdown, dropped.Text).Should().BeNull("nothing was lost in this run");

        LineParity.Compare("a\nb\nc", "a\nc").Should().Be("lines: 3 in source, 2 in output");
    }

    [TranslationAuditFact]
    public async Task An_intact_run_keeps_its_line_count_and_holds()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>("«" + text + "»"));

        result.StructureHeld.Should().BeTrue();
        LineParity.Compare(English, result.Text).Should().BeNull();
    }
}
