using System.Linq;
using BetterTranslator.Engine.Documents;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Where a line may be cut.
///
/// Splitting too eagerly is worse than not splitting at all: a sentence cut
/// through `.NET` or through a file path produces two fragments that translate
/// into nonsense, where an unsplit line merely translates less well.
/// </summary>
public sealed class SentenceSplitterTests
{
    private static string[] Split(string line) =>
        [.. SentenceSplitter.Split(line).Select(s => line.Substring(s.Start, s.Length))];

    [Fact]
    public void Sentences_are_cut_at_their_terminator_and_keep_it()
    {
        Split("First one. Second one. Third one.")
            .Should().Equal("First one.", "Second one.", "Third one.");
    }

    [Fact]
    public void Questions_and_exclamations_end_sentences_too() =>
        Split("Did it work? It did! Good.")
            .Should().Equal("Did it work?", "It did!", "Good.");

    [Theory]
    // The dot is inside the token, so there is no whitespace after it.
    [InlineData("STACK: C# WPF on .NET 10 and nothing else")]
    [InlineData("REPO: H:\\Software Development\\BetterTranslator")]
    [InlineData("The file is called i18n.json and it lives here")]
    [InlineData("Version 2.4 shipped on time")]
    // Lower case after the dot is not a new sentence.
    [InlineData("Approximately 3 in. of clearance is needed")]
    // A trailing terminator has nothing after it to start a sentence.
    [InlineData("One whole sentence.")]
    public void A_line_with_no_real_boundary_stays_whole(string line) =>
        Split(line).Should().ContainSingle().Which.Should().Be(line.Trim());

    [Fact]
    public void An_initial_is_not_the_end_of_a_sentence() =>
        Split("Written by J. Smith last year.")
            .Should().ContainSingle();

    [Fact]
    public void The_whitespace_between_sentences_stays_in_the_document()
    {
        // The spans cover the sentences only, so splicing a translation over one
        // cannot eat the spacing around it.
        const string Line = "First.   Second.";
        var spans = SentenceSplitter.Split(Line);

        spans.Should().HaveCount(2);
        spans[0].Should().Be((0, 6));
        spans[1].Should().Be((9, 7));
    }

    [Fact]
    public void The_real_line_that_failed_splits_into_its_three_sentences()
    {
        const string Line =
            "FEATURE: JSON-aware mode for the same message textbox. Primary use case is i18n resource files: "
            + "JSON objects mapping string keys to translatable text values. Keys can appear in any order and "
            + "values can nest arbitrarily, so translation must operate per value, never on the blob.";

        var sentences = Split(Line);

        sentences.Should().HaveCount(3);
        sentences[0].Should().Be("FEATURE: JSON-aware mode for the same message textbox.");
        sentences[2].Should().EndWith("never on the blob.");

        // Nothing is lost or duplicated: the pieces are the line.
        string.Concat(sentences).Replace(" ", string.Empty)
            .Should().Be(Line.Replace(" ", string.Empty));
    }

    [Fact]
    public void An_empty_line_reports_one_empty_span_rather_than_nothing() =>
        SentenceSplitter.Split(string.Empty).Should().ContainSingle();
}
