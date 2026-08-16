using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The pass that finishes the job.
///
/// A document out of the translate passes is not yet done: some lines came back
/// with a clause still in the source language, some with a word welded to a
/// protected term. Neither is visible to the gates that ran on them -- a line
/// with three English words in it loses no markup, invents nothing and is the
/// right length.
/// </summary>
public sealed class FinalRepairPassTests
{
    private static readonly IReadOnlyList<string> Terms =
        BetterTranslator.Engine.Slop.DoNotTranslate.Load().Strict;

    private static TranslationJob Job => new()
    {
        Text = string.Empty,
        ModelPath = @"C:\models\whatever.gguf",
        Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
    };

    private sealed class Recorder
    {
        public List<string> Translated { get; } = [];

        public List<string> Questions { get; } = [];

        public string? Answer { get; set; }

        public Func<string, string?> Translation { get; set; } = _ => null;

        public Func<TranslationJob, CancellationToken, Task<UnitOutcome>> Translate =>
            (job, _) =>
            {
                Translated.Add(job.Text);
                var text = Translation(job.Text);
                return Task.FromResult(new UnitOutcome(text, text is null ? 0 : 3, text is null ? "refused" : null));
            };

        public Func<string, string, CancellationToken, Task<string?>> Ask =>
            (_, question, _) =>
            {
                Questions.Add(question);
                return Task.FromResult(Answer);
            };
    }

    [Fact]
    public async Task AnUntouchedLineIsNeverExamined()
    {
        // An untranslated line is source by definition, and every word of it
        // would read as residue.
        var source = new[] { "The build failed because the store was missing." };
        var lines = (string[])source.Clone();

        var recorder = new Recorder { Answer = "TRANSLATE" };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        recorder.Questions.Should().BeEmpty();
        tally.ChangedNothing.Should().BeTrue();
    }

    [Fact]
    public async Task AClauseLeftInEnglishIsRepairedInPlace()
    {
        var source = new[] { "The store resolves a model by name and falls back to the pinned revision." };
        var lines = new[] { "Úložiště vyhledá model podle jména and falls back to připnuté revizi." };

        var recorder = new Recorder { Translation = _ => "a vrací se k" };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        tally.FragmentsRepaired.Should().Be(1);
        recorder.Translated.Should().ContainSingle().Which.Should().Be("and falls back to");
        lines[0].Should().Contain("a vrací se k").And.NotContain("and falls back to");
    }

    [Fact]
    public async Task ALineThatIsMostlySourceIsTranslatedWholeRatherThanPatched()
    {
        // Measured: the fragment repair produced Czech for two clauses while
        // leaving "On Windows" English between them. A gate refused the mixture,
        // correctly, and two model calls were spent for nothing.
        var source = new[] { "On Windows you can also use the batch file to cross-build both binaries." };
        var lines = new[] { "On Windows you can also use the batch file to cross-build both binaries ." };

        var recorder = new Recorder { Translation = _ => "Ve Windows můžete také použít dávkový soubor." };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        tally.LinesRetranslated.Should().Be(1);
        tally.FragmentsRepaired.Should().Be(0);
        recorder.Translated.Should().ContainSingle().Which.Should().Be(source[0], "the whole line, not a fragment");
    }

    [Fact]
    public async Task SpacingIsRepairedByScriptAloneWithNoModelCall()
    {
        // A missing separator needs no translation -- the words on both sides are
        // already correct -- so inserting it is a text operation.
        var source = new[] { "Open the presentation in LM Studio for the details." };
        var lines = new[] { "Otevřete prezentaciLM Studio pro podrobnosti." };

        var recorder = new Recorder();

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        tally.SpacesInserted.Should().Be(1);
        lines[0].Should().Contain("prezentaci LM Studio");
        recorder.Translated.Should().BeEmpty("spacing costs no model call");
    }

    [Fact]
    public async Task AnAmbiguousFragmentIsPutToTheModelAndKeptWhenItSaysSo()
    {
        // "original assets" and "Web Audio" are both two English words surviving
        // in Czech output, and no mechanical test separates them.
        var source = new[] { "The build uses Web Audio to mix the preview track." };
        var lines = new[] { "Sestavení používá Web Audio k mixáži ukázkové stopy." };

        var recorder = new Recorder { Answer = "KEEP", Translation = _ => "Zvuk webu" };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        recorder.Questions.Should().NotBeEmpty("a short surviving run cannot be judged mechanically");
        tally.Adjudicated.Should().BeGreaterThan(0);
        tally.ChangedNothing.Should().BeTrue("KEEP changes nothing");
        lines[0].Should().Contain("Web Audio");
    }

    [Fact]
    public async Task WithNoAnswerAtAllNothingIsEdited()
    {
        // A malformed or missing answer must never be able to trigger an edit.
        var source = new[] { "The build uses Web Audio to mix the preview track." };
        var lines = new[] { "Sestavení používá Web Audio k mixáži ukázkové stopy." };

        var recorder = new Recorder { Answer = null, Translation = _ => "Zvuk webu" };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        tally.ChangedNothing.Should().BeTrue();
        recorder.Translated.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameFragmentIsPutToTheModelOnceAcrossTheWholeDocument()
    {
        var source = new[]
        {
            "The build uses Web Audio to mix the preview track.",
            "A second line where Web Audio is mentioned again here.",
        };

        var lines = new[]
        {
            "Sestavení používá Web Audio k mixáži ukázkové stopy.",
            "Druhý řádek, kde je Web Audio zmíněno znovu zde.",
        };

        var recorder = new Recorder { Answer = "KEEP" };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        recorder.Questions.Count(q => q.Contains("Web Audio", StringComparison.Ordinal))
            .Should().Be(1, "a document repeats the same fragment many times");

        tally.Adjudicated.Should().Be(1);
    }

    [Fact]
    public async Task ARefusedRepairLeavesTheLineExactlyAsItWas()
    {
        var source = new[] { "The store resolves a model by name and falls back to the pinned revision." };
        var lines = new[] { "Úložiště vyhledá model podle jména and falls back to připnuté revizi." };
        var before = lines[0];

        var recorder = new Recorder { Translation = _ => null };

        var tally = await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms);

        tally.ChangedNothing.Should().BeTrue();
        lines[0].Should().Be(before);
    }

    [Fact]
    public async Task ACancelStopsThePass()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var source = new[] { "The build failed." };
        var lines = new[] { "Sestavení selhalo." };

        var recorder = new Recorder();

        var act = async () => await new FinalRepairPass(recorder.Translate, recorder.Ask)
            .RunAsync(Job, source, lines, Terms, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ATranslationContainingADollarSignIsNotReadAsASubstitution()
    {
        // The replacement goes through Regex.Replace, where "$1" means a captured
        // group. A translation that legitimately contains one would otherwise come
        // out mangled or empty.
        var source = new[] { "The store resolves a model by name and falls back to the pinned revision." };
        var lines = new[] { "Úložiště vyhledá model podle jména and falls back to připnuté revizi." };

        var recorder = new Recorder { Translation = _ => "stojí $5 a $1" };

        await new FinalRepairPass(recorder.Translate, recorder.Ask).RunAsync(Job, source, lines, Terms);

        lines[0].Should().Contain("stojí $5 a $1");
    }
}
