using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The reported defect and the guards that caused it, pinned.
///
/// Measured against TranslateGemma before any change: every correct Czech
/// translation of the reported sentence was refused with "backtick count
/// changed: 0 in source, 6 in output", because the model helpfully wrapped
/// &lt;name&gt;, --out and --json in backticks. Two attempts, then both
/// sentences separately, all refused, so the line shipped in English with
/// nothing on screen saying why.
/// </summary>
public sealed class SpecCorpusFidelityTests
{
    [Fact]
    public void AddedCodeSpansAreDroppedWhenTheSourceHadNone()
    {
        const string Answer =
            "Výchozí pojmenování výstupu: `<name>.<to>.<ext>` vedle vstupu, pokud není zadáno `--out`. "
            + "Dávka vypíše jeden řádek stavu na soubor, nebo jeden záznam `--json` na soubor.";

        var repaired = ChunkIntegrity.DropInventedCodeSpans(SpecCorpus.ReportedDefect, Answer);

        repaired.Should().NotContain("`");
        repaired.Should().Contain("<name>.<to>.<ext>").And.Contain("--out");
        ChunkIntegrity.Check(SpecCorpus.ReportedDefect, repaired).Should().BeNull();
    }

    [Fact]
    public void ASourceWithBackticksOfItsOwnIsLeftAlone()
    {
        // There the counts carry real information: a lost pair turns code into
        // prose, so a mismatch stays a refusal rather than being repaired away.
        const string Source = "Run `dotnet test` before shipping.";
        const string Answer = "Před odesláním spusťte `dotnet test` a `dotnet build`.";

        ChunkIntegrity.DropInventedCodeSpans(Source, Answer).Should().Be(Answer);
        ChunkIntegrity.Check(Source, Answer).Should().NotBeNull();
    }

    [Fact]
    public void AWindowThatCameFromTheSourceIsNotAnInstructionLeak()
    {
        // "--from <lang>, --to <lang>, --json, --quiet" strips to the six-word
        // window "from lang to lang json quiet". It is in the prompt only because
        // it is in the source, and a correct translation must reproduce it.
        const string Source = "Shared options: --from <lang>, --to <lang>, --json, --quiet, --context-dir <path>.";
        const string Answer = "Sdílené možnosti: --from <lang>, --to <lang>, --json, --quiet, --context-dir <path>.";
        var prompt = "Translate the user's text into Czech.\n" + Source;

        OutputLeak.Check(Source, Answer, prompt).Should().BeNull();
    }

    [Fact]
    public void AWindowFromTheInstructionIsStillALeak()
    {
        const string Source = "The build is green.";
        const string Prompt = "You are a professional translator working into Czech today.\n" + Source;
        const string Answer = "You are a professional translator working into Czech today.";

        OutputLeak.Check(Source, Answer, Prompt).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, KeptCause.NoAnswer)]
    [InlineData("", KeptCause.NoAnswer)]
    [InlineData("   ", KeptCause.NoAnswer)]
    [InlineData("prvni\ndruhy", KeptCause.Reflowed)]
    public void EachWayAnAnswerFailsIsNamed(string? answer, KeptCause expected) =>
        MessageTranslation.Unusable(answer, "The build is green.").Should().Be(expected);

    [Fact]
    public void AnEchoedAnswerIsNamedAsAnEcho() =>
        MessageTranslation.Unusable("The build is green.", "The build is green.")
            .Should().Be(KeptCause.Echoed);

    [Fact]
    public void AUsableAnswerHasNoCause() =>
        MessageTranslation.Unusable("Sestavení je zelené.", "The build is green.").Should().BeNull();

    [Theory]
    [InlineData("Test")]
    [InlineData("Sport")]
    [InlineData("  Hotel  ")]
    public void ASingleWordThatComesBackUnchangedIsATranslation(string word) =>
        MessageTranslation.Unusable(word, word).Should().BeNull();

    [Fact]
    public async Task AOneWordMessageWhoseCzechIsTheSameWordIsNotReportedAsUntranslated()
    {
        var result = await MessageTranslation.TranslateAsync("Test", (text, _) => Task.FromResult<string?>(text), CancellationToken.None);

        result.Translated.Should().Be(1);
        result.Kept.Should().Be(0);
        result.Text.Should().Be("Test");
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryRequestIsRecordedWithItsOutcome()
    {
        var result = await MessageTranslation.TranslateAsync(
            "The build is green.\nThe tests pass.",
            (text, _) => Task.FromResult<string?>(text == "The tests pass." ? null : "Sestavení je zelené."),
            CancellationToken.None,
            () => "leak: answer repeats the instructions");

        result.Translated.Should().Be(1);
        result.Kept.Should().Be(1);
        result.Units.Should().HaveCount(2);

        var kept = result.Failures.Single();
        kept.Source.Should().Be("The tests pass.");
        kept.Delivered.Should().Be(kept.Source);
        kept.Reason.Should().Contain("no answer came back").And.Contain("repeats the instructions");
    }

    [Fact]
    public async Task AKeptUnitCarriesTheGuardThatRefusedIt()
    {
        var result = await MessageTranslation.TranslateAsync(
            SpecCorpus.ReportedDefect,
            (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None,
            () => "backtick count changed: 0 in source, 6 in output");

        result.Text.Should().Be(SpecCorpus.ReportedDefect);
        result.Failures.Should().NotBeEmpty();
        result.Failures.Should().OnlyContain(u => u.Reason!.Contains("backtick count changed"));
    }

    [Fact]
    public async Task ASuccessfulRunLeavesNoFailureRecorded()
    {
        var result = await MessageTranslation.TranslateAsync(
            "The build is green.",
            (_, _) => Task.FromResult<string?>("Sestavení je zelené."),
            CancellationToken.None);

        result.Failures.Should().BeEmpty();
        result.Units.Should().ContainSingle().Which.FromModel.Should().BeTrue();
    }

    [Fact]
    public void TheAssertionsCatchEachDefectClass()
    {
        const string Source = "Survey Core, Engine, and Runtime to locate the entry points.";

        var act = () => FidelityAsserts.SurvivesVerbatim(
            Source,
            "Prozkoumejte jádro, motor a prostředí a najděte vstupní body.",
            SpecCorpus.MustSurviveVerbatim);
        act.Should().Throw<Xunit.Sdk.XunitException>("a translated component name is a defect");

        var echoed = () => FidelityAsserts.NoUntranslatedProse(Source, Source);
        echoed.Should().Throw<Xunit.Sdk.XunitException>("a surviving clause is a defect");

        var sentinel = () => FidelityAsserts.NoSentinelResidue("Prozkoumejte [[1]] a najděte vstupní body.");
        sentinel.Should().Throw<Xunit.Sdk.XunitException>("a sentinel reaching the reader is a defect");
    }

    [Fact]
    public void TheCorpusCoversEveryFamily()
    {
        SpecCorpus.All.Should().HaveCount(5);
        SpecCorpus.All.Should().OnlyContain(f => f.Text.Length > 0);
        SpecCorpus.CommandSurface.Should().Contain(SpecCorpus.ReportedDefect);
    }

    [Fact]
    public void AnglePlaceholdersAreHiddenFromTheModel()
    {
        // D14: they used to come back as <nazev>.<cil>.<rozsireni>, and every
        // gate passed it because the counts matched and the brackets balanced.
        // Now they never reach the model at all.
        var guards = PlaceholderGuard.Protect(SpecCorpus.ReportedDefect);

        guards.Text.Should().NotContain("<name>").And.NotContain("<to>").And.NotContain("<ext>");

        var answer = guards.Text.Replace(
            "Default output naming",
            "Výchozí pojmenování výstupu",
            System.StringComparison.Ordinal);

        PlaceholderGuard.Holds(answer, guards).Should().BeTrue();

        FidelityAsserts.SurvivesVerbatim(
            SpecCorpus.ReportedDefect,
            PlaceholderGuard.Restore(answer, guards),
            ["<name>", "<to>", "<ext>", "--out", "--json"]);
    }
}
