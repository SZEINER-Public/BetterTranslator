using BetterTranslator.Engine.Markup;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The two gates that misfire on a sentence somebody typed, and the two
/// relaxations that are off unless the caller says the text is standalone.
///
/// Both cases are measured rather than invented: they are what the local model
/// actually returned for two sentences of a real message, and what the gates
/// did with those answers.
/// </summary>
public sealed class StandaloneGuardTests
{
    private const string EightWords = "FEATURE: JSON-aware mode for the same message textbox.";

    /// <summary>Twelve words, and a correct translation of the line above.</summary>
    private const string TwelveWords =
        "FUNKCE: Režim s podporou formátu JSON pro stejné textové pole pro zprávy.";

    [Fact]
    public void A_short_sentence_sits_on_the_wrong_side_of_the_proportional_cliff()
    {
        // Eight source words allow eleven; seven would allow eighteen. The
        // answer is twelve, so a good translation is refused for being one word
        // past a ceiling that a shorter sentence clears by six.
        OutputLeak.Check(EightWords, TwelveWords)
            .Should().Contain("content was invented");
    }

    [Fact]
    public void Raising_the_floor_accepts_it_without_accepting_invention()
    {
        OutputLeak.Check(EightWords, TwelveWords, prompt: null, proportionalWordFloor: 20)
            .Should().BeNull();

        // The gate is relaxed, not removed. Twice the source plus four is still
        // the ceiling, and a model that started writing an essay is still caught.
        var essay = string.Join(' ', Enumerable.Repeat("slovo", 40));

        OutputLeak.Check(EightWords, essay, prompt: null, proportionalWordFloor: 20)
            .Should().Contain("content was invented");
    }

    [Fact]
    public void Past_the_raised_floor_the_proportional_rule_is_back()
    {
        // Twenty-five source words, so the proportional ceiling applies even for
        // a standalone sentence: a third longer is where invention shows.
        var source = string.Join(' ', Enumerable.Repeat("word", 25));
        var doubled = string.Join(' ', Enumerable.Repeat("slovo", 50));

        OutputLeak.Check(source, doubled, prompt: null, proportionalWordFloor: 20)
            .Should().Contain("content was invented");
    }

    [Fact]
    public void A_clarification_in_brackets_is_damage_in_a_document_and_not_in_a_sentence()
    {
        const string Source = "Primary use case is i18n resource files.";
        const string Translated = "Hlavním případem užití jsou soubory prostředků i18n (internacionalizace).";

        ChunkIntegrity.Check(Source, Translated)
            .Should().Contain("bracket count changed");

        ChunkIntegrity.Check(Source, Translated, allowAddedBrackets: true)
            .Should().BeNull();
    }

    [Theory]
    // Unbalanced: an opener with no closer is damage whatever the caller says.
    [InlineData("Plain source with no brackets.", "Prostý text (bez závorek.")]
    // Square brackets follow the same rule as round ones.
    [InlineData("Plain source with no brackets.", "Prostý text [bez závorek.")]
    public void An_unbalanced_addition_is_still_refused(string source, string translated) =>
        ChunkIntegrity.Check(source, translated, allowAddedBrackets: true)
            .Should().Contain("bracket count changed");

    [Fact]
    public void A_bracket_the_source_had_and_the_answer_lost_is_still_refused()
    {
        // The relaxation only covers brackets the source never had. Losing one it
        // did have is exactly the damage the gate exists for.
        const string Source = "See the changelog (published weekly) for more.";

        ChunkIntegrity.Check(Source, "Viz seznam změn publikovaný týdně pro více informací.", allowAddedBrackets: true)
            .Should().Contain("bracket count changed");
    }

    [Fact]
    public void Both_relaxations_are_off_unless_they_are_asked_for()
    {
        // The document pipeline calls these with defaults, so its behaviour --
        // and the parity fixtures that pin it -- are untouched.
        OutputLeak.Check(EightWords, TwelveWords).Should().NotBeNull();

        ChunkIntegrity.Check(
            "Primary use case is i18n resource files.",
            "Hlavním případem užití jsou soubory prostředků i18n (internacionalizace).")
            .Should().NotBeNull();
    }
}
