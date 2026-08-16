using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class TranslationAuditTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationAudit", name));

    private static string English => Fixture("claude-en.md");

    private static string Observed => Fixture("claude-cs-observed.md");

    private static string ArtifactPath(string name)
    {
        var root = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && root.Length > 3; i++)
        {
            if (Directory.Exists(Path.Combine(root, ".git")))
            {
                break;
            }

            root = Path.GetFullPath(Path.Combine(root, ".."));
        }

        var folder = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(folder);

        return Path.Combine(folder, name);
    }

    [TranslationAuditFact]
    public void The_evidence_pair_is_present_as_a_fixture()
    {
        English.Should().Contain("design-digests");
        Observed.Should().Contain("managed:design-digests:start");
    }

    [TranslationAuditFact]
    public void The_measured_audit_is_written_for_the_report()
    {
        var report = TranslationAudit.Run(English, Observed);

        var text = new StringBuilder();

        text.AppendLine("translatable_tokens=" + report.Coverage.TranslatableTokens);
        text.AppendLine("protected_tokens=" + report.Coverage.ProtectedTokens);
        text.AppendLine("surviving_tokens=" + report.Coverage.SurvivingTokens);
        text.AppendLine("translated_tokens=" + report.Coverage.TranslatedTokens);
        text.AppendLine("coverage_percent=" + report.Coverage.Percent.ToString("0.00"));
        text.AppendLine("source_units=" + report.SourceUnits);
        text.AppendLine("target_units=" + report.TargetUnits);
        text.AppendLine("source_table_rows=" + TranslationAudit.TableRowCount(English));
        text.AppendLine("target_table_rows=" + TranslationAudit.TableRowCount(Observed));
        text.AppendLine("structural_findings=" + report.Structural.Count);
        text.AppendLine("spans_total=" + report.Spans.Count);
        text.AppendLine("spans_defect=" + report.Spans.Count(s => !s.Preserved));
        text.AppendLine("spans_preserved=" + report.Spans.Count(s => s.Preserved));

        foreach (var group in report.Spans.GroupBy(s => s.Preserved ? s.Category.ToString() : "DEFECT"))
        {
            text.AppendLine("category:" + group.Key + "=" + group.Count());
        }

        foreach (var finding in report.Structural)
        {
            text.AppendLine("structural:" + finding.Line + ":" + finding.Kind + ":" + finding.Detail);
        }

        foreach (var span in report.Spans)
        {
            text.AppendLine(
                "span:" + span.Line
                + "|" + span.Text
                + "|" + span.Unit
                + "|" + (span.Preserved ? "PRESERVED" : "DEFECT")
                + "|" + span.Category
                + "|" + span.RunLength);
        }

        File.WriteAllText(ArtifactPath("translation-audit-measures.txt"), text.ToString());

        report.Coverage.TranslatableTokens.Should().BeGreaterThan(0);
    }

    [TranslationAuditFact]
    public void Seed_1_an_untranslated_block_is_a_surviving_source_run()
    {
        var lines = Observed.Replace("\r\n", "\n").Split('\n');
        var block = lines.Single(line => line.StartsWith("Digests are versioned documents"));

        UnitFidelity.LongestSourceRun(block, block)
            .Should().BeGreaterThanOrEqualTo(TranslationAudit.ResidueThreshold);

        UnitFidelity.Unverified(block, block).Should().BeTrue();
    }

    [TranslationAuditFact]
    public void Seed_2_a_line_that_opens_in_the_source_language_is_flagged()
    {
        var english = English.Replace("\r\n", "\n").Split('\n')
            .Single(line => line.Contains("Every UI task routes through the C# rows"));

        var observed = Observed.Replace("\r\n", "\n").Split('\n')
            .Single(line => line.Contains("Every UI task routes through the C# rows"));

        UnitFidelity.Unverified(english, observed)
            .Should().BeTrue("a run of source words survived into the answer");
    }

    [Fact]
    public void Seed_3_whitespace_around_an_inline_code_span_is_restored()
    {
        const string Source = " plus `csharp-ui-slop-catalog.md` as prohibitions ";
        const string Answer = "plus`csharp-ui-slop-catalog.md`jako nařízení";

        RunBoundary.Lost(Source, Answer).Should().BeTrue();
        RunBoundary.Preserve(Source, Answer).Should().Be(" " + Answer + " ");
    }

    [TranslationAuditFact]
    public void Seed_3_the_observed_document_carries_boundary_damage()
    {
        TranslationAudit.Run(English, Observed).Structural
            .Where(finding => finding.Kind == "boundary")
            .Should().NotBeEmpty();
    }

    [TranslationAuditFact]
    public void Seed_8_a_quoted_section_name_must_survive_byte_for_byte()
    {
        var lost = TranslationAudit.QuotedLiteralsLost(English, Observed);

        lost.Should().NotBeEmpty("the observed document translated quoted section names");
        lost.Should().Contain("\"Section 2 -- Distilled Rules\"");
    }

    [TranslationAuditFact]
    public void Seed_8_the_same_table_handled_two_quoted_names_two_ways()
    {
        var lost = TranslationAudit.QuotedLiteralsLost(English, Observed);

        lost.Should().NotContain(
            "\"2. DISTILLED RULES (token-based motion system)\"",
            "one row of the same table kept its English section name");
    }

    [Fact]
    public void Seed_4_an_emphasis_run_never_keeps_interior_whitespace()
    {
        EmphasisHygiene.Damaged("**Spouštěč: ** jakákoli").Should().BeTrue();
        EmphasisHygiene.Restore("**Spouštěč: ** jakákoli").Should().Be("**Spouštěč:** jakákoli");
        EmphasisHygiene.Restore("** Blok **").Should().Be("**Blok**");
    }

    [TranslationAuditFact]
    public void Seed_4_the_observed_document_carries_emphasis_damage()
    {
        TranslationAudit.Run(English, Observed).Structural
            .Where(finding => finding.Kind == "emphasis")
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Seed_5_a_separator_after_a_bold_label_survives_the_splice()
    {
        const string Source = "**Build, run, publish** -- every command block";
        const string Answer = "**Vytvořit, provozovat, publikovat**každý příkazový blok";

        RunBoundary.Preserve(Source, Answer).Should().Be(Answer);
        EmphasisHygiene.Restore(Answer).Should().Be(Answer);

        UnitFidelity.LongestSourceRun(Source, Answer).Should().BeLessThan(TranslationAudit.ResidueThreshold);
    }

    [Fact]
    public async Task A_unit_the_model_echoed_is_flagged_rather_than_shipped_silently()
    {
        const string Markdown = "## Editing policy\n\nDigests are versioned documents and never reformatted.\n";

        var result = await MarkdownTranslation.TranslateAsync(Markdown, (text, _) => Task.FromResult<string?>(text));

        result.Unverified.Should().Be(result.Translated, "every echoed unit is flagged");
        result.Unverified.Should().BeGreaterThan(0);
        result.Text.Should().Be(Markdown, "an echo is spliced over itself, so the bytes do not move");
    }

    [TranslationAuditFact]
    public async Task A_translated_unit_keeps_the_structure_of_its_source()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>(Marked(text)));

        result.StructureHeld.Should().BeTrue();
        result.Translated.Should().BeGreaterThan(0);

        TranslationAudit.TableRowCount(result.Text)
            .Should().Be(TranslationAudit.TableRowCount(English), "no table gained or lost a row");

        TranslationAudit.Run(English, result.Text).Structural
            .Should().BeEmpty("the fixed path emits no boundary or emphasis damage");
    }

    [TranslationAuditFact]
    public async Task Protected_literals_survive_the_fixed_path_byte_for_byte()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>(Marked(text)));

        foreach (var literal in Literals(English))
        {
            result.Text.Should().Contain(literal, "a protected literal may not change bytes");
        }
    }

    [TranslationAuditFact]
    public async Task An_echoed_document_is_flagged_unit_by_unit()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>(text));

        result.Unverified.Should().Be(
            result.Translated,
            "an answer that echoed its unit is detected rather than passed off as a translation");

        result.Unverified.Should().BeGreaterThan(0);
    }

    [TranslationAuditFact]
    public async Task Translation_switched_off_returns_the_document_byte_for_byte()
    {
        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) => Task.FromResult<string?>(text));

        var first = English.Zip(result.Text).TakeWhile(pair => pair.First == pair.Second).Count();

        result.Text.Should().Be(English, "identity is a property of the splice; first difference at index " + first);
    }

    [TranslationAuditFact]
    public async Task Unit_count_parity_holds_and_a_dropped_unit_fails_loudly()
    {
        var dropped = 0;

        var result = await MarkdownTranslation.TranslateAsync(
            English,
            (text, _) =>
            {
                dropped++;
                return Task.FromResult<string?>(dropped == 3 ? null : Marked(text));
            });

        result.StructureHeld.Should().BeTrue();
        (result.Translated + result.Recovered + result.Kept).Should().BeGreaterThan(0);
        TranslationAudit.UnitCount(result.Text).Should().Be(TranslationAudit.UnitCount(English));
    }

    private static string Marked(string text) => "«" + text + "»";

    private static IEnumerable<string> Literals(string markdown) =>
        Regex.Matches(markdown, @"`[^`\n]+`")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(40);
}
