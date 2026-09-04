using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Subtitles;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

public sealed class StructureCheckTests(ITestOutputHelper output)
{
    private const string Markdown =
        """
        ---
        title: Release notes for the desktop client
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

    private const string Subtitle =
        """
        1
        00:00:01,000 --> 00:00:03,250
        Open the settings panel.

        2
        00:00:03,400 --> 00:00:06,000
        Pin the sidebar to your workspace.

        3
        00:00:06,100 --> 00:00:08,000
        Done.
        """;

    private static string Guillemets(string text) => "«" + text + "»";

    private static Task<string?> Mark(string text, System.Threading.CancellationToken _)
    {
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("[[", System.StringComparison.Ordinal) && line.IndexOf(' ', System.StringComparison.Ordinal) is > 0 and var space)
            {
                lines[i] = line[..(space + 1)] + Guillemets(line[(space + 1)..]);
            }
            else
            {
                lines[i] = Guillemets(line);
            }
        }

        return Task.FromResult<string?>(string.Join('\n', lines));
    }

    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) =>
        CheckRegistry.Default.Find(checkId)!.Run(context);

    private static IReadOnlyList<CheckFinding> RunAll(CheckContext context) =>
        [.. CheckRegistry.Default.ForCategory(CheckId.Structure.Category).SelectMany(check => check.Run(context))];

    private static async Task<(string Text, IReadOnlyList<SegmentTrace> Segments)> Translate(string source)
    {
        var result = await ContentTranslation.TranslateAsync(source, Mark);
        result.Text.Should().NotBeNull();
        return (result.Text!, result.Segments);
    }

    [Fact]
    public void Nine_structure_checks_register_in_order()
    {
        var ids = CheckRegistry.Default.ForCategory(CheckId.Structure.Category).Select(check => check.CheckId).ToList();

        ids.Should().Equal(
            CheckId.Structure.ChunkParity,
            CheckId.Structure.JsonParity,
            CheckId.Structure.SubtitleParity,
            CheckId.Structure.MarkdownParity,
            CheckId.Structure.TableParity,
            CheckId.Structure.PlaceholderCensus,
            CheckId.Structure.PlaceholderDefect,
            CheckId.Structure.BoundaryIntegrity,
            CheckId.Structure.InvariantMultiset);
    }

    [Fact]
    public async Task Chunk_parity_passes_on_an_intact_run_and_fires_on_a_dropped_block()
    {
        var (text, segments) = await Translate(Markdown);
        var intact = StructureContext.Build(Markdown, text, MarkdownStructure.Instance, segments);

        Run(CheckId.Structure.ChunkParity, intact).Should().BeEmpty();

        var dropped = text.Replace("> «Upgrading from 2.3 needs no migration. Your settings carry over.»\n\n", string.Empty, System.StringComparison.Ordinal);
        dropped.Should().NotBe(text);

        var findings = Run(CheckId.Structure.ChunkParity, StructureContext.Build(Markdown, dropped, MarkdownStructure.Instance, segments));

        findings.Should().ContainSingle(f => f.Evidence.Contains("dropped", System.StringComparison.Ordinal))
            .Which.Should().Match<CheckFinding>(f =>
                f.SourceRange != null
                && f.Severity == CheckSeverity.Defect
                && f.Granularity == CheckGranularity.Block
                && f.CauseCode.Length > 0);
    }

    [Fact]
    public async Task Json_parity_passes_on_an_intact_run_and_fires_on_a_renamed_key()
    {
        var (text, segments) = await Translate(SpecCorpus.ResourceJson);
        var intact = StructureContext.Build(SpecCorpus.ResourceJson, text, JsonStructure.Instance, segments);

        Run(CheckId.Structure.JsonParity, intact).Should().BeEmpty();

        var renamed = text.Replace("\"title\":", "\"titul\":", System.StringComparison.Ordinal);
        var findings = Run(CheckId.Structure.JsonParity, StructureContext.Build(SpecCorpus.ResourceJson, renamed, JsonStructure.Instance, segments));

        findings.Should().Contain(f => f.Evidence.Contains("key 'title'", System.StringComparison.Ordinal) && f.Evidence.Contains("missing", System.StringComparison.Ordinal));
        findings.Should().Contain(f => f.Evidence.Contains("key 'titul'", System.StringComparison.Ordinal) && f.Evidence.Contains("added", System.StringComparison.Ordinal));
        findings.Should().OnlyContain(f => f.Severity == CheckSeverity.Defect && f.Confidence == 100);

        var broken = text.Replace("\"save\": ", "\"save\": \"Ulož", System.StringComparison.Ordinal);

        Run(CheckId.Structure.JsonParity, StructureContext.Build(SpecCorpus.ResourceJson, broken, JsonStructure.Instance, segments))
            .Should().ContainSingle()
            .Which.Should().Match<CheckFinding>(f => f.Evidence.Contains("well-formed", System.StringComparison.Ordinal) && f.CauseCode == CheckCause.Restore);
    }

    [Fact]
    public void Json_parity_fires_on_array_length_and_value_type_changes()
    {
        const string Source = """{ "tags": ["a", "b", "c"], "retries": 3, "nested": { "deep": true } }""";
        const string Target = """{ "tags": ["a", "b"], "retries": "3", "nested": { "deep": true } }""";

        var findings = Run(CheckId.Structure.JsonParity, StructureContext.Build(Source, Target, JsonStructure.Instance));

        findings.Should().Contain(f => f.Evidence.Contains("array length '3' in source, '2' in target", System.StringComparison.Ordinal));
        findings.Should().Contain(f => f.Evidence.Contains("value type 'Number' in source, 'String' in target", System.StringComparison.Ordinal));
        findings.Should().Contain(f => f.Evidence.Contains("value at /$/tags/2 missing", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Json_parity_survives_keys_that_look_like_paths_and_inflected_names_are_not_lost_identifiers()
    {
        const string Source = """{ "a/b": "x", "a": { "b": "y" }, "c@key": "z", "d": "GitHub and iPhone", "e": "Use System.IO.File." }""";
        const string Target = """{ "a/b": "x", "a": { "b": "y" }, "c@key": "z", "d": "GitHubu a iPhonu", "e": "Použijte System.IO.File." }""";

        var context = StructureContext.Build(Source, Target, JsonStructure.Instance);

        Run(CheckId.Structure.JsonParity, context).Should().BeEmpty();
        Run(CheckId.Structure.InvariantMultiset, context).Should().BeEmpty();

        var lost = StructureContext.Build(Source, Target.Replace("System.IO.File.", "System.IO.Soubor.", System.StringComparison.Ordinal), JsonStructure.Instance);

        Run(CheckId.Structure.InvariantMultiset, lost)
            .Should().Contain(f => f.Evidence == "identifier 'System.IO.File' lost: 1 in source, 0 in target");

        SpliceMap.Locate("AAAAAAAAAAAAAAAA\n\nBB\n\nCC", "AAAA\n\nBB\n\nCC", [(0, 16, null), (18, 2, null), (22, 2, null)])
            .Should().Equal([(0, 4), (6, 2), (10, 2)]);
    }

    [Fact]
    public void Subtitle_parity_passes_when_only_text_changes_and_fires_when_a_timecode_drifts_one_millisecond()
    {
        var translated = Subtitle
            .Replace("Open the settings panel.", "Otevřete panel nastavení.", System.StringComparison.Ordinal)
            .Replace("Pin the sidebar to your workspace.", "Připněte postranní panel.", System.StringComparison.Ordinal)
            .Replace("Done.", "Hotovo.", System.StringComparison.Ordinal);

        SubtitleSyntax.LooksLikeSubtitle(Subtitle).Should().BeTrue();
        StructureContext.AdapterFor(Subtitle).Should().BeSameAs(SubtitleStructure.Instance);

        Run(CheckId.Structure.SubtitleParity, StructureContext.Build(Subtitle, translated)).Should().BeEmpty();

        var drifted = translated.Replace("00:00:06,000", "00:00:06,001", System.StringComparison.Ordinal);
        var findings = Run(CheckId.Structure.SubtitleParity, StructureContext.Build(Subtitle, drifted));

        findings.Should().ContainSingle()
            .Which.Should().Match<CheckFinding>(f =>
                f.Evidence == "cue end timecode '00:00:06,000' in source, '00:00:06,001' in target"
                && f.Severity == CheckSeverity.Defect
                && f.Confidence == 100);

        var lost = translated.Replace("\n3\n00:00:06,100 --> 00:00:08,000\nHotovo.", string.Empty, System.StringComparison.Ordinal);

        Run(CheckId.Structure.SubtitleParity, StructureContext.Build(Subtitle, lost))
            .Should().Contain(f => f.Evidence == "cue count 3 in source, 2 in target" && f.Granularity == CheckGranularity.Document);

        var renumbered = translated.Replace("\n2\n00:00:03,400", "\n4\n00:00:03,400", System.StringComparison.Ordinal);

        Run(CheckId.Structure.SubtitleParity, StructureContext.Build(Subtitle, renumbered))
            .Should().ContainSingle(f => f.Evidence == "cue index '2' in source, '4' in target");
    }

    [Fact]
    public async Task Markdown_parity_passes_on_an_intact_run_and_fires_on_heading_fence_and_list_damage()
    {
        var (text, segments) = await Translate(Markdown);
        var intact = StructureContext.Build(Markdown, text, MarkdownStructure.Instance, segments);

        Run(CheckId.Structure.MarkdownParity, intact).Should().BeEmpty();

        var damaged = text
            .Replace("## «What changed»", "# «What changed»", System.StringComparison.Ordinal)
            .Replace("```bash", "```sh", System.StringComparison.Ordinal)
            .Replace("  - «macOS 14 lands next month»\n", string.Empty, System.StringComparison.Ordinal)
            .Replace("![«The sync indicator»](https://bubble.example.com/sync.png)", "«The sync indicator»", System.StringComparison.Ordinal);

        var findings = Run(CheckId.Structure.MarkdownParity, StructureContext.Build(Markdown, damaged, MarkdownStructure.Instance, segments));

        findings.Should().Contain(f => f.Evidence == "heading 'level 2' became 'level 1'");
        findings.Should().Contain(f => f.Evidence == "code fence '``` bash' became '``` sh'");
        findings.Should().Contain(f => f.Evidence == "list 'bullet 2 items' became 'bullet 1 items'");
        findings.Should().Contain(f => f.Evidence == "image 'https://bubble.example.com/sync.png' missing from target");
        findings.Should().OnlyContain(f => f.Severity == CheckSeverity.Defect);
    }

    [Fact]
    public async Task Table_parity_passes_on_an_intact_run_and_fires_on_a_lost_row_or_a_changed_delimiter()
    {
        var (text, segments) = await Translate(Markdown);

        Run(CheckId.Structure.TableParity, StructureContext.Build(Markdown, text, MarkdownStructure.Instance, segments)).Should().BeEmpty();

        var shortRow = text.Replace("| «Retry» | 3 | «Backs off each time» |\n", string.Empty, System.StringComparison.Ordinal);

        Run(CheckId.Structure.TableParity, StructureContext.Build(Markdown, shortRow, MarkdownStructure.Instance, segments))
            .Should().ContainSingle(f => f.Evidence == "table rows '3' in source, '2' in target");

        var realigned = text.Replace("| --- | --- | --- |", "| :-- | --- | --- |", System.StringComparison.Ordinal);

        Run(CheckId.Structure.TableParity, StructureContext.Build(Markdown, realigned, MarkdownStructure.Instance, segments))
            .Should().ContainSingle(f => f.Evidence == "table delimiter row '| --- | --- | --- |' in source, '| :-- | --- | --- |' in target");

        var narrow = text.Replace("| «Watch» | «on» | «Follows the folder» |", "| «Watch» | «on» |", System.StringComparison.Ordinal);

        Run(CheckId.Structure.TableParity, StructureContext.Build(Markdown, narrow, MarkdownStructure.Instance, segments))
            .Should().Contain(f => f.Evidence == "table row 2 cells '3' in source, '2' in target");
    }

    private static CheckContext PlaceholderRun(string answer, string target)
    {
        const string Source = "Press <b>Save</b> now.";

        var trace = new SegmentTrace(
            0,
            Source.Length,
            SegmentOutcome.Translated,
            answer,
            [new MaskTrace("[[0]]", "<b>", ExemptionReason.FormatPlaceholder), new MaskTrace("[[1]]", "</b>", ExemptionReason.FormatPlaceholder)],
            target,
            0,
            target.Length);

        return StructureContext.Build(Source, target, ProseStructure.Instance, [trace]);
    }

    [Fact]
    public void Placeholder_census_passes_when_every_placeholder_returns_once_and_fires_on_loss_and_residue()
    {
        var intact = PlaceholderRun("Stiskněte [[0]]Uložit[[1]] nyní.", "Stiskněte <b>Uložit</b> nyní.");

        Run(CheckId.Structure.PlaceholderCensus, intact).Should().BeEmpty();

        var lost = PlaceholderRun("Stiskněte [[0]]Uložit nyní.", "Stiskněte <b>Uložit nyní.");
        var findings = Run(CheckId.Structure.PlaceholderCensus, lost);

        findings.Should().ContainSingle()
            .Which.Should().Match<CheckFinding>(f =>
                f.Evidence == "placeholders emitted 2, returned once 1; missing [[1]]"
                && f.Severity == CheckSeverity.Defect
                && f.CauseCode == CheckCause.ModelOutput
                && f.Action == CheckAction.Repair);

        var residue = PlaceholderRun("Stiskněte [ [0] ]Uložit[ [1] ] nyní.", "Stiskněte [ [0] ]Uložit[ [1] ] nyní.");
        var survived = Run(CheckId.Structure.PlaceholderCensus, residue);

        survived.Should().Contain(f => f.Evidence == "placeholder text '[ [0] ]' survives in the output" && f.CauseCode == CheckCause.Restore);
        survived.Should().Contain(f => f.Evidence.StartsWith("placeholders emitted 2", System.StringComparison.Ordinal) && f.Evidence.Contains("not restored [[0]],[[1]]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Placeholder_defect_marks_the_span_of_an_absent_duplicated_or_unrestored_placeholder()
    {
        Run(CheckId.Structure.PlaceholderDefect, PlaceholderRun("Stiskněte [[0]]Uložit[[1]] nyní.", "Stiskněte <b>Uložit</b> nyní."))
            .Should().BeEmpty();

        var lost = PlaceholderRun("Stiskněte [[0]]Uložit nyní.", "Stiskněte <b>Uložit nyní.");
        var absent = Run(CheckId.Structure.PlaceholderDefect, lost);

        absent.Should().ContainSingle()
            .Which.Should().Match<CheckFinding>(f =>
                f.Evidence == "placeholder [[1]] for '</b>' absent from the answer"
                && f.TargetRange.Offset == 0
                && f.TargetRange.Length == "Stiskněte <b>Uložit nyní.".Length
                && f.SourceRange!.Offset == "Press <b>Save".Length
                && f.SourceRange.Length == "</b>".Length
                && f.Severity == CheckSeverity.Defect
                && f.CauseCode == CheckCause.ModelOutput);

        var doubled = PlaceholderRun("Stiskněte [[0]]Uložit[[1]] [[1]] nyní.", "Stiskněte <b>Uložit</b> </b> nyní.");

        Run(CheckId.Structure.PlaceholderDefect, doubled)
            .Should().ContainSingle(f => f.Evidence == "placeholder [[1]] for '</b>' duplicated 2 times");

        var unrestored = PlaceholderRun("Stiskněte [ [0] ]Uložit[[1]] nyní.", "Stiskněte [ [0] ]Uložit</b> nyní.");

        Run(CheckId.Structure.PlaceholderDefect, unrestored)
            .Should().ContainSingle(f => f.Evidence == "placeholder [[0]] returned but '<b>' was not restored" && f.CauseCode == CheckCause.Restore);
    }

    [Fact]
    public async Task Boundary_integrity_passes_on_an_intact_run_and_fires_when_a_translated_word_fuses_with_a_protected_name()
    {
        const string Source = "Open the \"Bubble Sync\" service and **pin** it.\n";

        var intact = await MarkdownTranslation.TranslateAsync(Source, (text, _) => Task.FromResult<string?>(Guillemets(text)));

        Run(CheckId.Structure.BoundaryIntegrity, StructureContext.Build(Source, intact.Text, MarkdownStructure.Instance, intact.Segments))
            .Should().BeEmpty();

        var fused = await MarkdownTranslation.TranslateAsync(
            Source,
            (text, _) => Task.FromResult<string?>(text.Replace("Open the [[0]] service and [[1]]pin[[2]] it.", "Otevřete službu[[0]] a [[1]]připněte[[2]] ji.", System.StringComparison.Ordinal)));

        fused.Text.Should().Contain("službu\"Bubble Sync\"", "the fixture glues a translated word to the protected name");

        var findings = Run(CheckId.Structure.BoundaryIntegrity, StructureContext.Build(Source, fused.Text, MarkdownStructure.Instance, fused.Segments));

        findings.Should().ContainSingle()
            .Which.Should().Match<CheckFinding>(f =>
                f.Evidence == "restored '\"Bubble Sync\"' fused on the left: Whitespace in source, Letter in target"
                && f.Severity == CheckSeverity.Defect
                && f.Granularity == CheckGranularity.Word
                && f.Confidence == 95
                && f.CauseCode == CheckCause.ModelOutput
                && f.TargetRange.Offset == fused.Text.IndexOf("u\"Bubble", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Invariant_multiset_scores_locale_classes_and_flags_identifier_and_path_loss_as_defects()
    {
        const string Source = "Version 2.4.1 ships on 2024-08-20 with --watch, costs $5, and lives in src/App/Main.cs beside System.IO.File.";
        const string Same = "Verze 2.4.1 vychází 2024-08-20 s --watch, stojí $5 a sídlí v src/App/Main.cs vedle System.IO.File.";

        Run(CheckId.Structure.InvariantMultiset, StructureContext.Build(Source, Same, ProseStructure.Instance)).Should().BeEmpty();

        const string Damaged = "Verze 2.4.2 vychází 20. 8. 2024 s --sledovat, stojí 5 USD a sídlí v src/App/Hlavni.cs vedle System.IO.File.";

        var findings = Run(CheckId.Structure.InvariantMultiset, StructureContext.Build(Source, Damaged, ProseStructure.Instance));

        findings.Should().Contain(f => f.Evidence == "identifier '--watch' lost: 1 in source, 0 in target" && f.Severity == CheckSeverity.Defect && f.Confidence == 95);
        findings.Should().Contain(f => f.Evidence == "filepath 'src/App/Main.cs' lost: 1 in source, 0 in target" && f.Severity == CheckSeverity.Defect);
        findings.Should().Contain(f => f.Evidence == "version '2.4.1' lost: 1 in source, 0 in target" && f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly);
        findings.Should().Contain(f => f.Evidence == "date '2024-08-20' lost: 1 in source, 0 in target" && f.Severity == CheckSeverity.Score);
        findings.Should().Contain(f => f.Evidence == "currency '$5' lost: 1 in source, 0 in target" && f.Severity == CheckSeverity.Score);
        findings.Should().NotContain(f => f.Evidence.Contains("System.IO.File", System.StringComparison.Ordinal));
        findings.Should().OnlyContain(f => f.Granularity == CheckGranularity.Word);
    }

    [Fact]
    public async Task A_span_in_the_exemption_registry_produces_no_structure_finding()
    {
        const string Source = "Open the \"Bubble Sync\" service and **pin** it.\n";

        var fused = await MarkdownTranslation.TranslateAsync(
            Source,
            (text, _) => Task.FromResult<string?>(text.Replace("Open the [[0]] service and [[1]]pin[[2]] it.", "Otevřete službu[[0]] a [[1]]připněte[[2]] ji.", System.StringComparison.Ordinal)));

        var unexempt = StructureContext.Build(Source, fused.Text, MarkdownStructure.Instance, fused.Segments);
        RunAll(unexempt).Should().NotBeEmpty();

        var exempt = StructureContext.Build(
            Source,
            fused.Text,
            MarkdownStructure.Instance,
            fused.Segments,
            [new ExemptSpan(new CheckRange(DocumentModel.RootPath, 0, fused.Text.Length), ExemptionReason.SettingsRule, fused.Text)]);

        RunAll(exempt).Should().BeEmpty();

        const string Prose = "Version 2.4.1 ships with --watch.";
        const string Translated = "Verze 2.4.1 vychází s --sledovat.";
        var lost = new CheckRange(DocumentModel.RootPath, 0, Translated.Length);

        RunAll(StructureContext.Build(Prose, Translated, ProseStructure.Instance)).Should().NotBeEmpty();
        RunAll(StructureContext.Build(Prose, Translated, ProseStructure.Instance, null, [new ExemptSpan(lost, ExemptionReason.GlossaryDoNotTranslate, "--sledovat")]))
            .Should().BeEmpty();
    }

    [Fact]
    public void Restore_puts_the_original_back_for_a_sentinel_the_model_drifted()
    {
        var guards = PlaceholderGuard.Protect("Rename <in> to <out> before publishing.");

        PlaceholderGuard.Holds("Přejmenujte [ [0] ] na 【1】 před publikováním.", guards).Should().BeTrue();

        PlaceholderGuard.Restore("Přejmenujte [ [0] ] na 【1】 před publikováním.", guards)
            .Should().Be("Přejmenujte <in> na <out> před publikováním.");

        PlaceholderGuard.Residue("Přejmenujte [ [0] ] na 【1】.").Select(r => r.Text).Should().Equal("[ [0] ]", "【1】");
        PlaceholderGuard.Residue("Přejmenujte <in> na <out>.").Should().BeEmpty();
    }

    [Fact]
    public async Task Two_runs_over_the_same_input_produce_the_same_findings_in_the_same_order()
    {
        var (text, segments) = await Translate(Markdown);

        var damaged = text
            .Replace("## «What changed»", "# «What changed»", System.StringComparison.Ordinal)
            .Replace("| «Retry» | 3 | «Backs off each time» |\n", string.Empty, System.StringComparison.Ordinal)
            .Replace("«Windows 11 only»", "«Windows only»", System.StringComparison.Ordinal);

        var first = RunAll(StructureContext.Build(Markdown, damaged, MarkdownStructure.Instance, segments));
        var second = RunAll(StructureContext.Build(Markdown, damaged, MarkdownStructure.Instance, segments));

        first.Should().NotBeEmpty();
        first.Should().Equal(second);
        first.Should().OnlyContain(f => f.Confidence >= 0 && f.Confidence <= 100 && f.CauseCode.Length > 0);
    }

    [Fact]
    public async Task The_fixture_corpus_produces_no_structure_finding_on_an_intact_run()
    {
        var corpus = new (string Name, string Text)[]
        {
            ("markdown", Markdown),
            ("subtitle", Subtitle),
            ("mixed-markdown", SpecCorpus.MixedMarkdown),
            ("resource-json", SpecCorpus.ResourceJson),
            ("command-surface", SpecCorpus.CommandSurface),
            ("policy-blocks", SpecCorpus.PolicyBlocks),
            ("component-names", SpecCorpus.ComponentNames),
            ("reported-defect", SpecCorpus.ReportedDefect),
            ("fixture-markdown", Fixtures.MarkdownBody),
            ("fixture-json", Fixtures.JsonBody),
            ("fixture-text", Fixtures.TextBody),
        };

        var counts = CheckRegistry.Default.ForCategory(CheckId.Structure.Category).ToDictionary(c => c.CheckId, _ => 0, System.StringComparer.Ordinal);
        var offenders = new System.Collections.Generic.List<string>();

        foreach (var (name, source) in corpus)
        {
            string target;
            IReadOnlyList<SegmentTrace> segments;

            if (SubtitleSyntax.LooksLikeSubtitle(source))
            {
                target = string.Join('\n', source.Split('\n').Select(line =>
                    line.Trim().Length == 0 || line.Trim().All(char.IsDigit) || line.Contains("-->", System.StringComparison.Ordinal)
                        ? line
                        : Guillemets(line)));
                segments = [];
            }
            else
            {
                (target, segments) = await Translate(source);
            }

            var context = StructureContext.Build(source, target, StructureContext.AdapterFor(source), segments);

            foreach (var check in CheckRegistry.Default.ForCategory(CheckId.Structure.Category))
            {
                foreach (var finding in check.Run(context).Where(f => f.Severity != CheckSeverity.Advisory))
                {
                    counts[check.CheckId]++;
                    offenders.Add($"{name}: {finding.CheckId} {finding.Evidence}");
                }
            }
        }

        foreach (var (id, count) in counts.OrderBy(pair => pair.Key, System.StringComparer.Ordinal))
        {
            output.WriteLine($"intact corpus {id}: {count}");
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task The_damaged_corpus_fires_every_check_at_least_once()
    {
        var (markdown, markdownSegments) = await Translate(Markdown);
        var (json, jsonSegments) = await Translate(SpecCorpus.ResourceJson);

        var damagedMarkdown = markdown
            .Replace("## «What changed»", "# «What changed»", System.StringComparison.Ordinal)
            .Replace("```bash", "```sh", System.StringComparison.Ordinal)
            .Replace("| «Retry» | 3 | «Backs off each time» |\n", string.Empty, System.StringComparison.Ordinal)
            .Replace("> «Upgrading from 2.3 needs no migration. Your settings carry over.»\n\n", string.Empty, System.StringComparison.Ordinal)
            .Replace("«Windows 11 only»", "«Windows only»", System.StringComparison.Ordinal);

        var damagedJson = json
            .Replace("\"title\":", "\"titul\":", System.StringComparison.Ordinal)
            .Replace("https://example.com/docs/getting-started", "https://example.com/docs/zacatek", System.StringComparison.Ordinal);

        var damagedSubtitle = Subtitle.Replace("00:00:06,000", "00:00:06,001", System.StringComparison.Ordinal);

        var contexts = new[]
        {
            StructureContext.Build(Markdown, damagedMarkdown, MarkdownStructure.Instance, markdownSegments),
            StructureContext.Build(SpecCorpus.ResourceJson, damagedJson, JsonStructure.Instance, jsonSegments),
            StructureContext.Build(Subtitle, damagedSubtitle, SubtitleStructure.Instance),
            PlaceholderRun("Stiskněte [ [0] ]Uložit nyní.", "Stiskněte [ [0] ]Uložit nyní."),
            StructureContext.Build(
                "Open the \"Bubble Sync\" service.\n",
                "Otevřete službu\"Bubble Sync\".\n",
                MarkdownStructure.Instance,
                [new SegmentTrace(0, "Open the \"Bubble Sync\" service.".Length, SegmentOutcome.Translated, "Otevřete službu[[0]].", [new MaskTrace("[[0]]", "\"Bubble Sync\"", ExemptionReason.ProtectedName)], "Otevřete službu\"Bubble Sync\".")]),
        };

        var counts = CheckRegistry.Default.ForCategory(CheckId.Structure.Category).ToDictionary(c => c.CheckId, _ => 0, System.StringComparer.Ordinal);

        foreach (var context in contexts)
        {
            foreach (var check in CheckRegistry.Default.ForCategory(CheckId.Structure.Category))
            {
                counts[check.CheckId] += check.Run(context).Count;
            }
        }

        foreach (var (id, count) in counts.OrderBy(pair => pair.Key, System.StringComparer.Ordinal))
        {
            output.WriteLine($"damaged corpus {id}: {count}");
        }

        counts.Values.Should().OnlyContain(count => count > 0);
    }
}
