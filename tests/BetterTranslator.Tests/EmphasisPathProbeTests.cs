using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class EmphasisPathProbeTests
{
    private static string English =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationAudit", "claude-en.md"));

    [TranslationAuditFact]
    public void The_unit_holding_a_bold_label_carries_the_delimiters_as_guards()
    {
        var unit = MarkdownSegmenter.Segment(English)
            .Single(candidate => candidate.Text.Contains("any change to the csproj", StringComparison.Ordinal));

        unit.Guards.Count.Should().BeGreaterThan(0);
        unit.Runs.Count.Should().BeGreaterThan(0);

        // The opening delimiter sits before the unit and only the closing one is
        // a guard, which is why an emphasis run can only be judged once the
        // document has been reassembled.
        unit.Text.Should().NotStartWith("**");
        unit.Guards.Should().Contain(guard => guard.Original == "**");
    }

    [Fact]
    public async Task An_answer_that_adds_a_space_before_a_closing_delimiter_is_repaired()
    {
        const string Markdown = "**Trigger:** any change to the csproj here.\n";

        var sent = string.Empty;

        var result = await MarkdownTranslation.TranslateAsync(
            Markdown,
            (text, _) =>
            {
                sent = text;
                return Task.FromResult<string?>(SpaceBeforeEverySentinel(text));
            });

        EmphasisHygiene.Damaged(result.Text).Should().BeFalse(
            "sent=[" + sent + "] out=[" + result.Text.TrimEnd('\n')
            + "] translated=" + result.Translated + " recovered=" + result.Recovered + " kept=" + result.Kept);
    }

    private static string SpaceBeforeEverySentinel(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\s)(\[\[\d+\]\])", " $1");
}
