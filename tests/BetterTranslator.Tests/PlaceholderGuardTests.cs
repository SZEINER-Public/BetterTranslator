using System.Linq;
using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Placeholders hidden behind sentinels so the model cannot translate them.
///
/// Measured before this existed: "&lt;name&gt;.&lt;to&gt;.&lt;ext&gt;" came back
/// as "&lt;název&gt;.&lt;cíl&gt;.&lt;rozšíření&gt;" and every gate passed it,
/// because the counts matched and the brackets balanced.
/// </summary>
public sealed class PlaceholderGuardTests
{
    [Fact]
    public void AnglePlaceholdersAreHiddenAndPutBack()
    {
        var guards = PlaceholderGuard.Protect(SpecCorpus.ReportedDefect);

        guards.Any.Should().BeTrue();
        guards.Originals.Should().Contain(["<name>", "<to>", "<ext>"]);
        guards.Text.Should().NotContain("<name>").And.Contain("[[0]]");

        // What a model would send back: the sentinels carried through a Czech
        // sentence, untouched.
        var answer = guards.Text
            .Replace("Default output naming", "Výchozí pojmenování výstupu", System.StringComparison.Ordinal);

        PlaceholderGuard.Holds(answer, guards).Should().BeTrue();
        PlaceholderGuard.Restore(answer, guards).Should().Contain("<name>.<to>.<ext>");
    }

    [Fact]
    public void ARenumberedOrDroppedSentinelDoesNotHold()
    {
        var guards = PlaceholderGuard.Protect("Rename <in> to <out> before publishing the file.");

        PlaceholderGuard.Holds("Přejmenujte [[0]] na neco pred publikovanim souboru.", guards)
            .Should().BeFalse("one sentinel was dropped");

        PlaceholderGuard.Holds("Přejmenujte [[1]] na [[0]] pred publikovanim souboru.", guards)
            .Should().BeFalse("swapping two placeholders names a different file and reads as correct");

        PlaceholderGuard.Holds("Přejmenujte [[0]] na [[1]] a [[2]] pred publikovanim.", guards)
            .Should().BeFalse("a sentinel was invented");
    }

    [Fact]
    public void BraceAndPrintfPlaceholdersAreProtectedToo()
    {
        var guards = PlaceholderGuard.Protect("You have {count} unread messages and %s errors.");

        guards.Originals.Should().Contain(["{count}", "%s"]);
        guards.Text.Should().NotContain("{count}").And.NotContain("%s");
    }

    [Fact]
    public void HtmlIsTheSameShapeAndGetsTheSameAnswer()
    {
        var guards = PlaceholderGuard.Protect("Press <b>Save</b> to keep the file.");

        guards.Originals.Should().Contain(["<b>", "</b>"]);
        PlaceholderGuard.Restore(guards.Text, guards).Should().Be("Press <b>Save</b> to keep the file.");
    }

    [Theory]
    [InlineData("The build is green.")]
    [InlineData("Check whether a < b and c > d before shipping.")]
    [InlineData("")]
    public void OrdinaryProseIsNotProtected(string text) =>
        PlaceholderGuard.Protect(text).Any.Should().BeFalse();

    [Fact]
    public void TextThatIsNothingButMachineryIsNotProtected()
    {
        // Sending it spends a call to be handed the sentinels back. There was
        // never a translation to be had.
        PlaceholderGuard.Protect("<name>.<to>.<ext>").Any.Should().BeFalse();
        PlaceholderGuard.Protect("{count} {total}").Any.Should().BeFalse();
    }

    [Fact]
    public void TooManyPlaceholdersDropTheProtectionRatherThanAttemptIt()
    {
        // MarkupGuard's own lesson, and why its list was cut down: a small model
        // asked to echo a crowd of opaque tokens in order echoes none of them,
        // and the whole unit is lost rather than one placeholder.
        var crowded = string.Join(" ", Enumerable.Range(0, PlaceholderGuard.MaxSentinels + 1)
            .Select(i => $"<slot{i}>"))
            + " translate this sentence please.";

        PlaceholderGuard.Protect(crowded).Any.Should().BeFalse();
    }

    [Fact]
    public void TextThatAlreadyLooksSentinelledIsLeftAlone() =>
        PlaceholderGuard.Protect("Reproduce [[0]] exactly and rename <in> afterwards.").Any.Should().BeFalse();

    [Fact]
    public void AnEmptyGuardHoldsWhateverCameBack()
    {
        // The unprotected path: nothing was hidden, so nothing can have been lost.
        PlaceholderGuard.Holds("cokoliv", PlaceholderGuards.None).Should().BeTrue();
        PlaceholderGuard.Holds(null, PlaceholderGuards.None).Should().BeTrue();
    }

    [Theory]
    [InlineData("Přejmenujte [ [0] ] na [ [1] ].")]
    [InlineData("Přejmenujte [0] na [1].")]
    [InlineData("Přejmenujte [[ 0 ]] na [[ 1 ]].")]
    [InlineData("Přejmenujte 【0】 na 【1】.")]
    [InlineData("Přejmenujte ⟦0⟧ na ⟦1⟧.")]
    public void ASentinelTheModelDriftedStillCounts(string answer)
    {
        // Measured: the sentinel survives far more often than it arrives intact.
        // Every one of these still says exactly which slot it is, and reading
        // them is the difference between a placeholder surviving and a whole
        // line falling back to the structural pass.
        var guards = PlaceholderGuard.Protect("Rename <in> to <out> before publishing.");

        PlaceholderGuard.Holds(answer, guards).Should().BeTrue();
        PlaceholderGuard.Restore(PlaceholderGuard.Normalize(answer)!, guards)
            .Should().Contain("<in>").And.Contain("<out>");
    }

    [Fact]
    public void NormalizingLeavesOrdinaryBracketsAlone()
    {
        // A translation may legitimately carry a bracketed number that is not a
        // sentinel at all, but only where nothing was protected.
        PlaceholderGuard.Normalize("Kapitola [3] je hotová.").Should().Be("Kapitola [[3]] je hotová.");
        PlaceholderGuard.Normalize("Bez závorek.").Should().Be("Bez závorek.");
        PlaceholderGuard.Normalize(null).Should().BeNull();
    }

    [Fact]
    public void TheSourceCutsAtItsPlaceholders()
    {
        var runs = PlaceholderGuard.Split(SpecCorpus.ReportedDefect);

        runs.Where(r => r.IsPlaceholder).Select(r => r.Text)
            .Should().Equal("<name>", "<to>", "<ext>");

        // Reassembling the cut is the original, byte for byte. If that is not
        // true the structural pass corrupts every line it touches.
        string.Concat(runs.Select(r => r.Text)).Should().Be(SpecCorpus.ReportedDefect);
    }

    [Fact]
    public void ACutCarriesTheProseWorthTranslating()
    {
        var runs = PlaceholderGuard.Split("Rename <in> to <out>.");

        // The trailing "." is a run of its own and holds no letters, so it is
        // carried across rather than sent to be translated.
        runs.Where(PlaceholderGuard.IsProse).Select(r => r.Text.Trim())
            .Should().Equal("Rename", "to");

        // A run of punctuation alone is not prose and is carried, not sent.
        PlaceholderGuard.IsProse(new PlaceholderGuard.Run(".", false)).Should().BeFalse();
        PlaceholderGuard.IsProse(new PlaceholderGuard.Run("<in>", true)).Should().BeFalse();
    }

    [Fact]
    public void TheModelIsToldWhatTheTokensAre()
    {
        // The trained branch sends the model card's own instruction and drops the
        // engine's system prompt, so this note travelling with the text is the
        // only thing that explains the form on TranslateGemma.
        PlaceholderGuard.Instruction.Should().Contain("[[0]]").And.Contain("exactly");
    }

    [Fact]
    public void RestoringIsExactEvenPastTenSentinels()
    {
        var text = "Map <a> <b> <c> <d> <e> <f> <g> <h> across the file and translate this line.";
        var guards = PlaceholderGuard.Protect(text);

        guards.Originals.Should().HaveCount(8);
        PlaceholderGuard.Restore(guards.Text, guards).Should().Be(text);
    }
}
