using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BetterTranslator.Engine.Json;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What happens inside a single resource string: the machinery that must not be
/// translated, the escaping on the way back, and what a bad answer costs.
/// </summary>
public sealed class JsonValueTests
{
    private static Func<string, CancellationToken, Task<string?>> Model(Func<string, string?> answer) =>
        (text, _) => Task.FromResult(answer(text));

    [Theory]
    [InlineData("Deleted {count} of {total} files", 2)]
    [InlineData("Hello {{name}}, welcome back", 1)]
    [InlineData("Saved %s at %d percent", 2)]
    [InlineData("Retry %1$s of %2$s", 2)]
    [InlineData("Press <b>Save</b> to continue", 2)]
    [InlineData("Line one\nLine two", 1)]
    [InlineData("{count, plural, one {# file} other {# files}}", 1)]
    [InlineData("No machinery at all here", 0)]
    public void Machinery_inside_a_value_is_lifted_out_before_it_can_be_translated(string value, int expected)
    {
        var (text, guards) = JsonValueGuard.Protect(value);

        guards.Should().HaveCount(expected);
        JsonValueGuard.Restore(text, guards).Should().Be(value, "putting it back must be exact");

        foreach (var guard in guards)
        {
            text.Should().Contain(guard.Sentinel);
        }
    }

    [Fact]
    public async Task A_placeholder_survives_translation_with_its_spacing_intact()
    {
        const string Doc = """{"msg":"Deleted {count} of {total} files"}""";

        var result = await JsonTranslation.TranslateAsync(
            Doc,
            Model(request =>
            {
                // The model never sees a brace.
                request.Should().NotContain("{").And.NotContain("}");
                return request.Replace("Deleted", "Smazano").Replace("of", "z").Replace("files", "souboru");
            }));

        using var document = JsonDocument.Parse(result.Text);
        document.RootElement.GetProperty("msg").GetString()
            .Should().Be("Smazano {count} z {total} souboru");
    }

    [Fact]
    public void Tokens_are_not_fused_to_the_words_beside_them()
    {
        // `{count}files` would format into "3files". The sentinel has to keep
        // the space that was there.
        var (text, guards) = JsonValueGuard.Protect("Deleted {count} of {total} files");

        text.Should().Be("Deleted [[0]] of [[1]] files");
        guards.Select(g => g.Original).Should().Equal("{count}", "{total}");
    }

    [Theory]
    // Quotes and backslashes have to come back escaped or the document breaks.
    [InlineData("He said \"hi\"", false, "He said \\\"hi\\\"")]
    [InlineData("C:\\Users\\dev", false, "C:\\\\Users\\\\dev")]
    [InlineData("Line one\nLine two", false, "Line one\\nLine two")]
    [InlineData("Tab\there", false, "Tab\\there")]
    // Accents follow the document's own convention.
    [InlineData("Uloženo", true, "Uloženo")]
    [InlineData("Uloženo", false, "Ulo\\u017eeno")]
    public void A_translated_value_is_written_the_way_the_document_writes_JSON(
        string value,
        bool literal,
        string expected) =>
        JsonEscape.Content(value, literal).Should().Be(expected);

    [Fact]
    public async Task An_escaped_document_stays_escaped_and_a_literal_one_stays_literal()
    {
        // This document already spells its accents out, so a translation is
        // spelled out too rather than switching convention halfway down.
        const string Escaped = """{"a":"Save","b":"Ulo\u017eeno"}""";

        var escaped = await JsonTranslation.TranslateAsync(Escaped, Model(_ => "[[0]] Uloženo"));
        escaped.Text.Should().Contain("\\u017eeno").And.NotContain("Uloženo");

        const string Literal = """{"a":"Save","b":"Uloženo"}""";

        var literal = await JsonTranslation.TranslateAsync(Literal, Model(_ => "[[0]] Uloženo"));
        literal.Text.Should().Contain("Uloženo");
    }

    [Fact]
    public async Task A_value_whose_placeholders_come_back_wrong_keeps_its_source()
    {
        const string Doc = """{"msg":"Deleted {count} files","other":"Save"}""";

        var result = await JsonTranslation.TranslateAsync(
            Doc,
            // Drops every sentinel that is not a line marker -- that is, the
            // ones standing in for machinery inside a value. Written as a shape
            // rather than as fixed numbers, because which numbers a value's
            // slots get depends on how the batch was packed.
            Model(request => System.Text.RegularExpressions.Regex.Replace(
                request, @"(?<!^|\n)\[\[\d+\]\]", string.Empty)));

        using var document = JsonDocument.Parse(result.Text);

        document.RootElement.GetProperty("msg").GetString()
            .Should().Be("Deleted {count} files", "a dropped slot means the source is kept");
        result.Kept.Should().BeGreaterThan(0);
        result.KeptPaths.Should().Contain("msg");
    }

    [Fact]
    public async Task Nothing_coming_back_leaves_the_document_exactly_as_it_was()
    {
        const string Doc = """{"a":"Save","b":"Cancel"}""";

        var result = await JsonTranslation.TranslateAsync(Doc, Model(_ => null));

        result.Text.Should().Be(Doc);
        result.Translated.Should().Be(0);
        result.Kept.Should().Be(2);
    }

    [Fact]
    public async Task A_batch_that_loses_a_line_is_retried_one_value_at_a_time()
    {
        const string Doc = """{"a":"Save","b":"Cancel","c":"Delete"}""";
        var batched = 0;

        var result = await JsonTranslation.TranslateAsync(
            Doc,
            Model(request =>
            {
                if (request.Contains('\n'))
                {
                    // A batch. Drop the last line, as a model that stops early
                    // would.
                    batched++;
                    return string.Join('\n', request.Split('\n')[..^1]);
                }

                return "Prelozeno";
            }));

        batched.Should().Be(1, "it was tried as a batch first");

        using var document = JsonDocument.Parse(result.Text);
        document.RootElement.EnumerateObject().Select(p => p.Value.GetString())
            .Should().AllBe("Prelozeno", "every value was recovered individually");
    }

    [Fact]
    public void A_value_with_no_language_in_it_is_never_sent()
    {
        JsonValueGuard.WorthSending("Save").Should().BeTrue();
        JsonValueGuard.WorthSending("{count}").Should().BeFalse();
        JsonValueGuard.WorthSending("#FF0000").Should().BeFalse();
        JsonValueGuard.WorthSending("42").Should().BeFalse();
        JsonValueGuard.WorthSending("   ").Should().BeFalse();
        JsonValueGuard.WorthSending("%s").Should().BeFalse();
    }

    [Fact]
    public async Task A_document_with_nothing_worth_sending_is_left_alone()
    {
        const string Doc = """{"colour":"#FF0000","slot":"{count}","n":7}""";

        var result = await JsonTranslation.TranslateAsync(Doc, Model(_ => "translated"));

        result.Text.Should().Be(Doc);
        result.Translated.Should().Be(0);
        JsonSyntax.HasTranslatableValues(Doc).Should().BeFalse();
    }
}
