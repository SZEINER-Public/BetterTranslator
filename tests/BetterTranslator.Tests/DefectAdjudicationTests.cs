using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Asking the model about a defect no rule can settle.
///
/// The ambiguous set is not a gap in the detector -- it is where no local rule
/// can separate a product name that correctly stayed in English from a phrase the
/// model simply failed to translate.
/// </summary>
public sealed class DefectAdjudicationTests
{
    private static TranslationDefect Residue(string text) =>
        new(DefectKind.Residue, text, text.Split(' ').Length, DefectVerdict.Ambiguous, "ambiguous");

    private static TranslationDefect Glue(string text) =>
        new(DefectKind.Glue, text, 1, DefectVerdict.Ambiguous, "ambiguous");

    private static Func<string, string, CancellationToken, Task<string?>> Answers(string? answer) =>
        (_, _, _) => Task.FromResult(answer);

    [Fact]
    public void AVerdictMustBeValidForItsKind()
    {
        // Asked about the residue "joins authoritative sessions" the model
        // answered SPACE -- a spacing verdict for a phrase with no spacing fault,
        // which would have licensed an edit that makes no sense.
        DefectAdjudication.Parse("SPACE", DefectKind.Residue).Should().Be(AdjudicatedVerdict.Keep);
        DefectAdjudication.Parse("TRANSLATE", DefectKind.Glue).Should().Be(AdjudicatedVerdict.Keep);

        DefectAdjudication.Parse("SPACE", DefectKind.Glue).Should().Be(AdjudicatedVerdict.Space);
        DefectAdjudication.Parse("TRANSLATE", DefectKind.Residue).Should().Be(AdjudicatedVerdict.Translate);
    }

    [Fact]
    public void PunctuationAndCaseAroundTheWordAreIgnored()
    {
        DefectAdjudication.Parse("  translate.  ", DefectKind.Residue).Should().Be(AdjudicatedVerdict.Translate);
        DefectAdjudication.Parse("\"KEEP\"", DefectKind.Residue).Should().Be(AdjudicatedVerdict.Keep);
    }

    [Fact]
    public void AnythingUnrecognisedFallsToKeep()
    {
        // Keep changes nothing, which is what makes it the only safe default.
        DefectAdjudication.Parse("I think this should probably be translated", DefectKind.Residue)
            .Should().Be(AdjudicatedVerdict.Keep);
        DefectAdjudication.Parse("", DefectKind.Residue).Should().Be(AdjudicatedVerdict.Keep);
        DefectAdjudication.Parse(null, DefectKind.Glue).Should().Be(AdjudicatedVerdict.Keep);
    }

    [Fact]
    public async Task TheSameFragmentIsAskedAboutOnce()
    {
        // A defect list on a real document repeats the same fragment many times.
        var asked = 0;
        var adjudicator = new DefectAdjudication();

        Task<string?> Ask(string instruction, string question, CancellationToken token)
        {
            asked++;
            return Task.FromResult<string?>("TRANSLATE");
        }

        for (var i = 0; i < 5; i++)
        {
            var verdict = await adjudicator.AdjudicateAsync(
                Residue("joins authoritative sessions"), "src", "tgt", "English", "Czech", Ask);

            verdict.Should().Be(AdjudicatedVerdict.Translate);
        }

        asked.Should().Be(1);
        adjudicator.Asked.Should().Be(1);
    }

    [Fact]
    public async Task TheSameTextUnderADifferentKindIsADifferentQuestion()
    {
        var adjudicator = new DefectAdjudication();

        (await adjudicator.AdjudicateAsync(Residue("BubbleSurge"), "s", "t", "English", "Czech", Answers("TRANSLATE")))
            .Should().Be(AdjudicatedVerdict.Translate);

        (await adjudicator.AdjudicateAsync(Glue("BubbleSurge"), "s", "t", "English", "Czech", Answers("SPACE")))
            .Should().Be(AdjudicatedVerdict.Space);

        adjudicator.Asked.Should().Be(2);
    }

    [Fact]
    public async Task AFailedRequestIsNotReadAsAgreeingToAChange()
    {
        var adjudicator = new DefectAdjudication();

        var verdict = await adjudicator.AdjudicateAsync(
            Residue("Bubble Surge"), "s", "t", "English", "Czech",
            (_, _, _) => throw new InvalidOperationException("no runtime"));

        verdict.Should().Be(AdjudicatedVerdict.Keep);
    }

    [Fact]
    public async Task ACancelIsNotSwallowed()
    {
        // A cancel is the operator stopping the run, not a model failing to
        // answer, and turning it into Keep would silently finish the pass.
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await new DefectAdjudication().AdjudicateAsync(
            Residue("Bubble Surge"), "s", "t", "English", "Czech",
            (_, _, token) => Task.FromCanceled<string?>(token),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void BothLinesGoWithTheFragment()
    {
        // Asked about a fragment alone the model has no way to tell a product
        // name from an untranslated clause.
        var question = DefectAdjudication.Question(
            "English", "Czech", "The build failed.", "Sestavení failed.", "failed");

        question.Should().Contain("The build failed.")
            .And.Contain("Sestavení failed.")
            .And.Contain("Fragment in question: failed");

        DefectAdjudication.Instruction("English", "Czech")
            .Should().Contain("exactly one word").And.Contain("No explanation.");
    }
}
