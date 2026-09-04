using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Naturalness;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Naturalness;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RewritePassTests
{
    private static readonly string[] Source = ["The file is saved.", "Open the settings window."];

    private static readonly string[] Target = ["Se soubor ukládá.", "Otevřete okno nastavení."];

    private const string Natural = "Soubor se ukládá.";

    private sealed class FixtureEmbeddings : IEmbeddingHost
    {
        public string ModelIdentity => "fixture";

        public bool Available => true;

        public string UnavailableReason => string.Empty;

        public int Calls => 0;

        private static readonly System.Collections.Generic.Dictionary<string, int> Concepts = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["file"] = 0, ["soubor"] = 0, ["saved"] = 1, ["ukládá"] = 1, ["open"] = 2, ["otevřete"] = 2, ["settings"] = 3, ["nastavení"] = 3, ["window"] = 4, ["okno"] = 4,
            ["neukládá"] = 5, ["nikdy"] = 6, ["nebyl"] = 7, ["dokument"] = 8, ["name"] = 9,
        };

        public float[]? Embed(string text)
        {
            var vector = new float[10];

            foreach (var word in text.Split([' ', '.', ',', '{', '}'], System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (Concepts.TryGetValue(word, out var index))
                {
                    vector[index] += 1;
                }
            }

            return vector.Any(v => v > 0) ? vector : [1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        }
    }

    private static VerificationPipeline Pipeline(NaturalnessSettings? settings = null, bool semantics = true, TerminologyGlossary? glossary = null)
    {
        var verification = new VerificationSettings { Naturalness = settings ?? new NaturalnessSettings { RewriteEnabled = true } };

        return new VerificationPipeline(null, verification)
        {
            Naturalness = _ => NaturalnessFixtures.Czech(NaturalnessFixtures.CalibratedForTests()),
            Semantics = _ => semantics ? new SemanticServices(new FixtureEmbeddings(), new UnavailableReverseTranslator("fixture")) : null,
            Terminology = _ => new TerminologyServices(new TableLemmatizer("cs").Add("soubor", "souboru", "soubory").Add("složka", "složky", "složku"), glossary ?? new TerminologyGlossary()),
        };
    }

    private static Func<RewriteRequest, CancellationToken, Task<RewriteAnswer>> Answering(string proposal) =>
        (_, _) => Task.FromResult(new RewriteAnswer(proposal, 5));

    private static (string Source, string Target, IReadOnlyList<Engine.Verification.Structure.SegmentTrace> Traces) Corpus() => NaturalnessFixtures.Lines(Source, Target);

    [Fact]
    public async Task An_accepted_rewrite_splices_by_range_and_keeps_the_rest()
    {
        var (source, target, traces) = Corpus();
        var pass = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, Answering(Natural));

        var outcome = await pass.RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);

        outcome.Accepted.Should().Be(1);
        outcome.Text.Should().Be(Natural + "\n" + Target[1]);
        outcome.Final.CompletionPercent.Should().Be(100.0);
        outcome.Final.Routed.Should().NotContain(r => r.CheckId == CheckId.Naturalness.CliticPlacement);
    }

    [Fact]
    public async Task A_rewrite_that_drops_a_glossary_term_is_rejected()
    {
        var (source, target, traces) = Corpus();
        var glossary = new TerminologyGlossary().Add("file", "soubor");
        var pass = new RewritePass(Pipeline(glossary: glossary), new NaturalnessSettings { RewriteEnabled = true }, Answering("Dokument se ukládá."));

        var outcome = await pass.RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);

        outcome.Accepted.Should().Be(0);
        outcome.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonTerminology);
        outcome.Text.Should().Be(target);
    }

    [Fact]
    public async Task A_rewrite_without_a_meaning_proof_is_rejected()
    {
        var (source, target, traces) = Corpus();
        var pass = new RewritePass(Pipeline(semantics: false), new NaturalnessSettings { RewriteEnabled = true }, Answering(Natural));

        var outcome = await pass.RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);

        outcome.Accepted.Should().Be(0);
        outcome.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonMeaningUnavailable);
        outcome.Text.Should().Be(target);
    }

    [Fact]
    public async Task A_rewrite_that_changes_meaning_is_rejected()
    {
        var (source, target, traces) = Corpus();
        var pass = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, Answering("Soubor se neukládá a nikdy nebyl."));

        var outcome = await pass.RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);

        outcome.Accepted.Should().Be(0);
        outcome.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonMeaning);
        outcome.Text.Should().Be(target);
    }

    [Fact]
    public async Task A_positional_placeholder_string_is_never_reordered()
    {
        var source = new[] { "The file %s is saved." };
        var target = new[] { "Se soubor %s ukládá." };
        var (sourceText, targetText, traces) = NaturalnessFixtures.Lines(source, target);
        var asked = 0;
        var pass = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, (_, _) => { asked++; return Task.FromResult(new RewriteAnswer("Soubor %s se ukládá.", 1)); });

        var outcome = await pass.RunAsync(sourceText, targetText, traces, "en", "cs", RepairAutonomy.RepairEverything);

        asked.Should().Be(0);
        outcome.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonPositional && d.Advisory);
        outcome.Text.Should().Be(targetText);
    }

    [Fact]
    public async Task A_second_pass_over_accepted_output_rewrites_nothing()
    {
        var (source, target, traces) = Corpus();
        var pass = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, Answering(Natural));

        var first = await pass.RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);
        var shifted = RewritePass.ShiftTraces(traces, new CheckRange("/0", 0, Target[0].Length), Natural.Length - Target[0].Length);
        var second = await pass.RunAsync(source, first.Text, shifted, "en", "cs", RepairAutonomy.AutoRepair);

        first.Accepted.Should().Be(1);
        second.Accepted.Should().Be(0);
        second.Requested.Should().Be(0);
        second.Text.Should().Be(first.Text);
    }

    [Fact]
    public async Task Autonomy_rungs_behave_as_specified()
    {
        var (source, target, traces) = Corpus();
        var settings = new NaturalnessSettings { RewriteEnabled = true, ImprovementMargin = 5 };

        var off = await new RewritePass(Pipeline(settings), settings, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.Off);
        off.Requested.Should().Be(0);
        off.SkipReason.Should().Be(RewritePass.ReasonOff);

        var ask = await new RewritePass(Pipeline(settings), settings, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AskEveryTime);
        ask.Requested.Should().Be(1);
        ask.Accepted.Should().Be(0);
        ask.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonAwaitingUser && d.Proposed == Natural);
        ask.Text.Should().Be(target);

        var auto = await new RewritePass(Pipeline(settings), settings, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);
        auto.Accepted.Should().Be(0);
        auto.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonMargin);

        var everything = await new RewritePass(Pipeline(settings), settings, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.RepairEverything);
        everything.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task The_pass_is_off_by_default_and_the_document_cap_holds()
    {
        var (source, target, traces) = Corpus();
        var defaults = new VerificationSettings();

        defaults.Naturalness.RewriteEnabled.Should().BeFalse();
        defaults.Autonomy.Should().Be(RepairAutonomy.AutoRepair);

        var outcome = await new RewritePass(Pipeline(defaults.Naturalness), defaults.Naturalness, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);
        outcome.Requested.Should().Be(0);

        var capped = new NaturalnessSettings { RewriteEnabled = true, RewritesPerDocument = 0 };
        var cappedOutcome = await new RewritePass(Pipeline(capped), capped, Answering(Natural)).RunAsync(source, target, traces, "en", "cs", RepairAutonomy.AutoRepair);
        cappedOutcome.Requested.Should().Be(0);
        cappedOutcome.Decisions.Should().OnlyContain(d => d.Reason == RewritePass.ReasonCap);
    }

    [Fact]
    public async Task An_accepted_rewrite_leaves_placeholders_and_completion_unchanged()
    {
        var source = new[] { "The file {name} is saved.", "Open the settings window." };
        var target = new[] { "Se soubor {name} ukládá.", "Otevřete okno nastavení." };
        var (sourceText, targetText, traces) = NaturalnessFixtures.Lines(source, target);
        var pass = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, Answering("Soubor {name} se ukládá."));

        var outcome = await pass.RunAsync(sourceText, targetText, traces, "en", "cs", RepairAutonomy.AutoRepair);

        outcome.Accepted.Should().Be(1);
        outcome.Text.Should().Contain("{name}");
        outcome.Final.CompletionPercent.Should().Be(100.0);

        var dropped = new RewritePass(Pipeline(), new NaturalnessSettings { RewriteEnabled = true }, Answering("Soubor se ukládá."));
        var rejected = await dropped.RunAsync(sourceText, targetText, traces, "en", "cs", RepairAutonomy.AutoRepair);
        rejected.Decisions.Should().ContainSingle(d => d.Reason == RewritePass.ReasonPlaceholders);
    }

    [Fact]
    public void Format_limits_classify_placeholders()
    {
        FormatLimits.Classify("Save %s now", "Uložte %s nyní", "prose").Should().Be(ReorderPermission.ForbiddenPositionalPlaceholders);
        FormatLimits.Classify("Save {0} now", "Uložte {0} nyní", "prose").Should().Be(ReorderPermission.Allowed);
        FormatLimits.Classify("Save {name} now", "Uložte {name} nyní", "json").Should().Be(ReorderPermission.Allowed);
        FormatLimits.Classify("Line", "Řádek\ndruhý", "subtitle").Should().Be(ReorderPermission.ForbiddenCueBoundary);
        FormatLimits.PlaceholdersSurvive("{0} a {1}", "{1} a {0}").Should().BeTrue();
        FormatLimits.PlaceholdersSurvive("{0} a {1}", "{0} a").Should().BeFalse();
    }
}
