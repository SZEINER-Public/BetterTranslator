using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A double-quoted span in a technical document names something: a section in
/// another file, a string the program prints, a value to type. It has to come
/// back byte for byte, quote characters included.
/// </summary>
public sealed class QuotedCitationTests
{
    private static string English =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationAudit", "claude-en.md"));

    private static string Marked(string text) => "«" + text + "»";

    [TranslationAuditFact]
    public void A_quoted_span_is_lifted_out_of_the_unit_it_sits_in()
    {
        var unit = MarkdownSegmenter.Segment(English)
            .Single(candidate => candidate.Guards.Any(guard => guard.Original.Contains("Distilled Actionable Rules")));

        unit.Text.Should().NotContain("Distilled Actionable Rules", "the model never sees the citation");
        unit.Guards.Should().Contain(guard => guard.Original == "\"Distilled Actionable Rules\"");
    }

    [TranslationAuditFact]
    public async Task Every_quoted_citation_survives_the_pipeline_byte_for_byte()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>(Marked(text)));

        result.StructureHeld.Should().BeTrue();

        TranslationAudit.QuotedLiteralsLost(English, result.Text)
            .Should().BeEmpty("a citation the source wrote is a citation the output keeps");
    }

    [Fact]
    public async Task A_cell_that_is_only_a_citation_is_never_sent()
    {
        const string Markdown = "| Task | Section |\n|---|---|\n| Build | \"Distilled Rules\" |\n";

        var sent = new System.Collections.Generic.List<string>();

        var result = await MarkdownTranslation.TranslateAsync(
            Markdown,
            (text, _) =>
            {
                sent.Add(text);
                return Task.FromResult<string?>(Marked(text));
            });

        sent.Should().NotContain(text => text.Contains("Distilled Rules", StringComparison.Ordinal));
        result.Text.Should().Contain("\"Distilled Rules\"", "the cell kept the citation it held");
    }

    [TranslationAuditFact]
    public async Task Switching_the_protection_off_puts_the_citation_back_in_the_unit()
    {
        BetterTranslator.Engine.Config.PipelineOptions.ProtectQuotedCitations = false;

        try
        {
            var sent = new System.Collections.Generic.List<string>();

            await MarkdownTranslation.TranslateAsync(
                English,
                (text, _) =>
                {
                    sent.Add(text);
                    return Task.FromResult<string?>(Marked(text));
                });

            sent.Should().Contain(text => text.Contains("Distilled Actionable Rules", StringComparison.Ordinal));
        }
        finally
        {
            BetterTranslator.Engine.Config.PipelineOptions.ProtectQuotedCitations = true;
        }
    }
}
