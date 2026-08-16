using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Engine.Documents;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// How an ordinary message is cut up before it is sent.
///
/// The case behind this is measured rather than imagined: a seven line brief
/// where three lines came back byte-identical because each was a paragraph of
/// technical prose sent whole, refused whole, and kept whole. The reader saw a
/// third of their message in the source language and nothing said which third.
/// </summary>
public sealed class MessageTranslationTests
{
    private static Func<string, CancellationToken, Task<string?>> Model(Func<string, string?> answer) =>
        (text, _) => Task.FromResult(answer(text));

    [Fact]
    public async Task A_one_line_message_is_one_call_with_the_whole_text()
    {
        const string Prose = "The build failed on the second stage.";
        var sent = new List<string>();

        var result = await MessageTranslation.TranslateAsync(Prose, Model(text =>
        {
            sent.Add(text);
            return "Sestaveni selhalo.";
        }));

        sent.Should().ContainSingle().Which.Should().Be(Prose, "nothing is gained by splitting one sentence");
        result.Text.Should().Be("Sestaveni selhalo.");
        result.Translated.Should().Be(1);
    }

    [Fact]
    public async Task A_line_the_model_will_not_answer_is_retried_sentence_by_sentence()
    {
        const string Line =
            "FEATURE: JSON-aware mode for the same textbox. Primary use case is i18n resource files. "
            + "Keys can appear in any order and values nest arbitrarily.";

        var sent = new List<string>();

        var result = await MessageTranslation.TranslateAsync(Line, Model(text =>
        {
            sent.Add(text);

            // Refuses the paragraph, answers a sentence. This is what the small
            // model actually did.
            return text.Length > 120 ? text : "CS " + text;
        }));

        result.Recovered.Should().Be(1);
        result.Kept.Should().Be(0);

        sent.Should().HaveCount(4, "once whole, then once per sentence");
        result.Text.Should().StartWith("CS FEATURE:");
        result.Text.Should().Contain("CS Primary use case");
        result.Text.Should().Contain("CS Keys can appear");
    }

    [Fact]
    public async Task A_line_that_is_one_sentence_is_not_asked_the_same_question_twice()
    {
        const string Line = "One sentence that nothing will translate no matter how often it is asked.";
        var sent = new List<string>();

        var result = await MessageTranslation.TranslateAsync(Line, Model(text =>
        {
            sent.Add(text);
            return text;
        }));

        sent.Should().ContainSingle("splitting found nothing the whole line did not already have");
        result.Kept.Should().Be(1);
        result.Text.Should().Be(Line);
    }

    [Fact]
    public async Task An_answer_identical_to_what_was_sent_counts_as_no_answer()
    {
        // A model echoing its input has not translated it, and writing it back
        // would be the source standing in for a translation.
        var result = await MessageTranslation.TranslateAsync("Nothing happens here.", Model(text => text));

        result.Translated.Should().Be(0);
        result.Kept.Should().Be(1);
    }

    [Fact]
    public async Task An_answer_carrying_a_line_break_is_refused()
    {
        // It would split one line into two and the message would stop lining up
        // with its source in the column beside it.
        var result = await MessageTranslation.TranslateAsync(
            "One line in.",
            Model(_ => "One line\nand another"));

        result.Text.Should().Be("One line in.");
        result.Kept.Should().Be(1);
    }

    [Fact]
    public async Task The_line_layout_of_the_message_is_kept_exactly()
    {
        const string Message = "First line.\n\n    Indented line.\n\nLast line.";

        var result = await MessageTranslation.TranslateAsync(Message, Model(text => "CS " + text));

        var lines = result.Text.Split('\n');

        lines.Should().HaveCount(5, "blank lines are lines too");
        lines[1].Should().BeEmpty();
        lines[2].Should().Be("    CS Indented line.", "the indent is the document's, not the model's");
        lines[3].Should().BeEmpty();
    }

    [Fact]
    public async Task A_line_with_nothing_to_translate_is_never_sent()
    {
        var sent = new List<string>();

        await MessageTranslation.TranslateAsync("Real prose here.\n\n---\n42\n", Model(text =>
        {
            sent.Add(text);
            return "CS " + text;
        }));

        sent.Should().NotContain("---").And.NotContain("42");
    }

    [Fact]
    public async Task Every_line_of_a_brief_gets_translated_rather_than_a_third_of_it()
    {
        // The shape that failed: several long, identifier-dense lines, one of
        // which is a bare path with nothing to translate in it.
        const string Brief =
            "<context>\n"
            + "PROJECT: A desktop translation tool for software teams. It runs models locally.\n"
            + "REPO: D:\\src\\BetterTranslator\n"
            + "STACK: C# WPF on .NET 10; six projects plus tests. Ships as a single self-contained file.\n"
            + "</context>";

        var result = await MessageTranslation.TranslateAsync(
            Brief,
            // Refuses anything long, exactly as the measured run did.
            Model(text => text.Length > 60 ? text : "CS " + text));

        result.Kept.Should().Be(0, "no line may be left in the source language without being tried smaller");
        result.Recovered.Should().Be(2, "the two multi-sentence lines were recovered");

        foreach (var line in result.Text.Split('\n'))
        {
            line.Should().Contain("CS ", "every line carries translated text");
        }
    }
}
