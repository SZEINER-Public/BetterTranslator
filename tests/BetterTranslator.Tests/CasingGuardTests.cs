using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Casing the source carried and the answer dropped.
///
/// Every case here is measured against TranslateGemma rather than imagined: the
/// labels are the ones from a real message, and the one that fails is the one
/// that failed in the application.
/// </summary>
public sealed class CasingGuardTests
{
    [Fact]
    public void TheLabelThatCameBackTitleCasedIsPutBackInCapitals() =>
        CasingGuard.Restore(
            "FEATURE: JSON-aware mode for the same message textbox.",
            "Funkce: Režim pro stejný textový vstup.")
            .Should().Be("FUNKCE: Režim pro stejný textový vstup.");

    [Theory]
    [InlineData("PROJECT: BetterTranslator is a tool.", "PROJEKT: BetterTranslator je nástroj.")]
    [InlineData("EXISTING: The textbox has a mode.", "STÁVAJÍCÍ: Pole má režim.")]
    [InlineData("STACK: C# WPF on .NET 10.", "STACK: C# WPF na .NET 10.")]
    public void ALabelTheModelAlreadyGotRightIsLeftAlone(string source, string translated) =>
        CasingGuard.Restore(source, translated).Should().Be(translated);

    [Fact]
    public void ASourceThatWasNotShoutingCannotMakeTheAnswerShout() =>
        CasingGuard.Restore(
            "Feature: a normally capitalised label.",
            "Funkce: štítek, který obvykle používá velká písmena.")
            .Should().Be("Funkce: štítek, který obvykle používá velká písmena.");

    [Fact]
    public void AWholeLineOfCapitalsStaysAWholeLineOfCapitals() =>
        CasingGuard.Restore("DO NOT BREAK EITHER BEHAVIOR", "nepouštějte žádné z těchto chování")
            .Should().Be("NEPOUŠTĚJTE ŽÁDNÉ Z TĚCHTO CHOVÁNÍ");

    [Fact]
    public void ASentenceThatOpenedWithACapitalStillDoes() =>
        CasingGuard.Restore("The build failed.", "sestavení selhalo.")
            .Should().Be("Sestavení selhalo.");

    [Fact]
    public void AValueThatOpenedLowerCaseIsNotForcedDown() =>
        CasingGuard.Restore("save changes", "Uložit změny").Should().Be("Uložit změny");

    [Fact]
    public void ASecondColonInTheSentenceIsNotMistakenForTheLabel() =>
        CasingGuard.Restore(
            "PROJECT: BetterTranslator: a desktop tool.",
            "Projekt: BetterTranslator: nástroj pro počítač.")
            .Should().Be("PROJEKT: BetterTranslator: nástroj pro počítač.");

    [Fact]
    public void AColonTooFarIntoTheAnswerIsLeftAlone()
    {
        // The model dropped the label, so the first colon belongs to the
        // sentence. Uppercasing to it would shout half a line.
        const string Translated =
            "Tento velmi dlouhý překlad ztratil svůj štítek a jeho první dvojtečka je až tady: ve větě.";

        CasingGuard.Restore("FEATURE: something.", Translated).Should().Be(Translated);
    }

    [Fact]
    public void AnAnswerWithNoColonIsLeftAlone() =>
        CasingGuard.Restore("FEATURE: something.", "Něco bez dvojtečky").Should().Be("Něco bez dvojtečky");

    [Theory]
    [InlineData(null, "Něco")]
    [InlineData("FEATURE: x", null)]
    [InlineData("", "")]
    public void NothingToWorkFromIsNotAFailure(string? source, string? translated) =>
        CasingGuard.Restore(source, translated).Should().Be(translated ?? string.Empty);

    [Fact]
    public void RestoringTwiceChangesNothingTheSecondTime()
    {
        var once = CasingGuard.Restore("FEATURE: mode.", "Funkce: režim.");
        var twice = CasingGuard.Restore("FEATURE: mode.", once);

        twice.Should().Be(once);
    }

    [Fact]
    public void ASingleCapitalIsASentenceOpeningRatherThanAShout() =>
        CasingGuard.Restore("A tool.", "nástroj.").Should().Be("Nástroj.");
}
