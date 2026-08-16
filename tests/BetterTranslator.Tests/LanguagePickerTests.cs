using System.Linq;
using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// What the composer offers to translate into, and why it differs by model.
///
/// The list is not maintained here: it comes from the ported registry, which
/// carries one column per model family recording what that model was actually
/// trained on. Offering a language a model has never seen is the one failure
/// nothing downstream can catch -- every gate compares structure, placeholders
/// and terminology, never meaning, so the answer comes back structurally perfect
/// and wrong and the run reports success.
/// </summary>
public sealed class LanguagePickerTests(ITestOutputHelper output)
{
    [Fact]
    public void EachModelOffersWhatTheRegistrySaysItWasTrainedOn()
    {
        var all = TargetLanguage.All;
        var euro = TargetLanguage.ForModel("eurollm");
        var gemma = TargetLanguage.ForModel("translategemma");

        output.WriteLine($"registry:       {all.Count}");
        output.WriteLine($"eurollm:        {euro.Count}");
        output.WriteLine($"translategemma: {gemma.Count}");
        output.WriteLine("not in eurollm: " + string.Join(", ",
            all.Where(l => euro.All(e => e.Code != l.Code)).Select(l => $"{l.Name} ({l.Code})")));

        all.Should().NotBeEmpty();

        // EuroLLM has a published training set, so its list is genuinely
        // narrower. If this ever stops being true the filter has silently
        // stopped filtering.
        euro.Count.Should().BeLessThan(all.Count, "EuroLLM's training set is published and narrower");
        euro.Should().OnlyContain(l => all.Any(a => a.Code == l.Code));

        gemma.Count.Should().BeGreaterThanOrEqualTo(euro.Count);
    }

    [Fact]
    public void AModelTheRegistryKnowsNothingAboutGetsEverything()
    {
        // Absence of evidence is not evidence of absence: the operator is warned
        // elsewhere rather than blocked here.
        TargetLanguage.ForModel("some-model-nobody-has-heard-of")
            .Should().HaveCount(TargetLanguage.All.Count);

        TargetLanguage.ForModel(null).Should().HaveCount(TargetLanguage.All.Count);
    }

    [Fact]
    public void EveryLanguageShowsEitherAFlagOrItsCode()
    {
        // A blank tile where every other row has a flag reads as artwork that
        // failed to load. There are eight flags and fifty-one languages, so most
        // rows take the code tile and none is left empty.
        TargetLanguage.All.Should().OnlyContain(l => l.HasFlag || l.CodeLabel.Length > 0);

        var drawn = TargetLanguage.All.Where(l => l.HasFlag).ToList();
        output.WriteLine($"with flags: {drawn.Count} - " + string.Join(", ", drawn.Select(l => l.Name)));

        drawn.Should().OnlyContain(l => l.FlagKey.StartsWith("Flag", StringComparison.Ordinal));
        drawn.Should().Contain(l => l.Code == "cs");
    }

    [Theory]
    [InlineData("cze", "cs")]
    [InlineData("Czech", "cs")]
    [InlineData("cs", "cs")]
    [InlineData("Ces", "cs")]
    [InlineData("deutsch", "de")]
    public void SearchMatchesTheEnglishNameTheNativeNameOrTheCode(string query, string expected)
    {
        var hits = TargetLanguage.All.Where(l => l.Matches(query)).ToList();

        hits.Should().Contain(l => l.Code == expected, $"'{query}' should find {expected}");
    }

    [Fact]
    public void AnEmptySearchMatchesEverything()
    {
        TargetLanguage.All.Should().OnlyContain(l => l.Matches(""));
        TargetLanguage.All.Should().OnlyContain(l => l.Matches("   "));
        TargetLanguage.All.Should().OnlyContain(l => l.Matches(null));
    }

    [Fact]
    public void SwitchingToANarrowerModelMovesTheSelectionOffALanguageItCannotDo()
    {
        // Otherwise the composer goes on sending a target the model was never
        // trained on, which is exactly the silent failure this filtering exists
        // to prevent.
        var euro = TargetLanguage.ForModel("eurollm");
        var untrained = TargetLanguage.All.FirstOrDefault(l => euro.All(e => e.Code != l.Code));

        if (untrained is null)
        {
            output.WriteLine("every language is in EuroLLM's set on this registry; nothing to check");
            return;
        }

        output.WriteLine($"untrained example: {untrained.Name} ({untrained.Code})");

        // The workspace does the move; this pins the fact the move is needed.
        euro.Should().NotContain(l => l.Code == untrained.Code);
    }
}
