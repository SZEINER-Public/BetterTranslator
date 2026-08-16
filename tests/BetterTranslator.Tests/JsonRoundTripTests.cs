using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BetterTranslator.Engine.Json;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The JSON path, held to the rule it exists for: the document that comes out is
/// the document that went in, with only its string values changed.
///
/// The model in these tests wraps whatever it is given in guillemets. Those
/// characters can appear nowhere else, so stripping them from the output has to
/// give the source back exactly -- a moved brace, a renamed key, a re-indented
/// line or a rewritten number would all leave a mark that stripping cannot
/// remove.
/// </summary>
public sealed class JsonRoundTripTests
{
    private const string Resource =
        """
        {
          "app": {
            "title": "Bubble Desktop",
            "subtitle": "One folder, every machine"
          },
          "actions": {
            "save": "Save",
            "cancel": "Cancel",
            "delete": "Delete"
          },
          "status": {
            "synced": "Deleted {count} of {total} files",
            "retries": 3,
            "watching": true,
            "lastError": null,
            "tags": ["fast", "safe"]
          }
        }
        """;

    private static Func<string, CancellationToken, Task<string?>> Model(Func<string, string?> answer) =>
        (text, _) => Task.FromResult(answer(text));

    /// <summary>Marks every line of a batch, so a batched answer stays splittable.</summary>
    private static string Mark(string request) =>
        string.Join('\n', request.Split('\n').Select(line =>
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            return line.StartsWith("[[", StringComparison.Ordinal) && space > 0
                ? line[..space] + " «" + line[(space + 1)..] + "»"
                : "«" + line + "»";
        }));

    private static string Unmark(string text) =>
        text.Replace("«", string.Empty).Replace("»", string.Empty);

    [Fact]
    public async Task Only_string_values_change_and_everything_else_is_byte_identical()
    {
        var result = await JsonTranslation.TranslateAsync(Resource, Model(Mark));

        result.Kept.Should().Be(0);
        result.Text.Should().NotBe(Resource);
        Unmark(result.Text).Should().Be(Resource, "nothing outside a string value may have moved");
    }

    [Theory]
    // Keys, in their original order and spelling.
    [InlineData("\"app\": {")]
    [InlineData("\"subtitle\":")]
    [InlineData("\"lastError\":")]
    // Non-string values are not language.
    [InlineData("\"retries\": 3")]
    [InlineData("\"watching\": true")]
    [InlineData("\"lastError\": null")]
    public async Task What_must_not_change_does_not(string fragment)
    {
        var result = await JsonTranslation.TranslateAsync(Resource, Model(Mark));

        result.Text.Should().Contain(fragment);
    }

    [Fact]
    public async Task Keys_are_never_sent_to_the_model_at_all()
    {
        var sent = new List<string>();

        await JsonTranslation.TranslateAsync(Resource, Model(text =>
        {
            sent.Add(text);
            return Mark(text);
        }));

        var everything = string.Join('\n', sent);

        foreach (var key in new[] { "app", "subtitle", "actions", "cancel", "retries", "lastError", "tags" })
        {
            everything.Should().NotContain($"\"{key}\"");
        }

        // Not merely unquoted-and-missed: the key names do not occur.
        everything.Should().NotContain("subtitle").And.NotContain("lastError");
    }

    [Fact]
    public async Task The_output_still_parses_and_still_says_the_same_things()
    {
        var result = await JsonTranslation.TranslateAsync(Resource, Model(Mark));

        using var document = JsonDocument.Parse(result.Text);
        var root = document.RootElement;

        root.GetProperty("status").GetProperty("retries").GetInt32().Should().Be(3);
        root.GetProperty("status").GetProperty("watching").GetBoolean().Should().BeTrue();
        root.GetProperty("status").GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("app").GetProperty("title").GetString().Should().Be("«Bubble Desktop»");
        root.GetProperty("status").GetProperty("tags").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("«fast»", "«safe»");
    }

    [Fact]
    public async Task Minified_input_comes_back_minified()
    {
        const string Minified = """{"a":{"title":"Save","n":1},"b":["One","Two"]}""";

        var result = await JsonTranslation.TranslateAsync(Minified, Model(Mark));

        Unmark(result.Text).Should().Be(Minified);
        result.Text.Should().NotContain("\n", "nothing re-indented it");
    }

    [Fact]
    public async Task Key_order_is_whatever_the_document_said_it_was()
    {
        const string Unsorted = """{"zebra":"Stripes","alpha":"First","middle":"Between"}""";

        var result = await JsonTranslation.TranslateAsync(Unsorted, Model(Mark));

        result.Text.IndexOf("zebra", StringComparison.Ordinal)
            .Should().BeLessThan(result.Text.IndexOf("alpha", StringComparison.Ordinal));
        Unmark(result.Text).Should().Be(Unsorted);
    }

    /// <summary>
    /// Two keys holding the same word get their own answers written to their own
    /// places. Mapping by value rather than by span would write one of them
    /// twice and lose the other.
    /// </summary>
    [Fact]
    public async Task Duplicate_values_under_different_keys_do_not_collide()
    {
        const string Duplicated = """{"a":"Open","b":"Open","c":"Open"}""";
        var call = 0;

        var result = await JsonTranslation.TranslateAsync(
            Duplicated,
            Model(request => string.Join('\n', request.Split('\n').Select(line =>
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                return line[..space] + " Otevrit" + (++call);
            }))));

        using var document = JsonDocument.Parse(result.Text);
        var values = document.RootElement.EnumerateObject().Select(p => p.Value.GetString()).ToList();

        values.Should().OnlyHaveUniqueItems("each key got its own answer");
        values.Should().HaveCount(3);
    }

    [Fact]
    public async Task An_array_of_strings_is_translated_element_by_element()
    {
        const string Array = """["First", "Second", "Third"]""";

        var result = await JsonTranslation.TranslateAsync(Array, Model(Mark));

        using var document = JsonDocument.Parse(result.Text);
        document.RootElement.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("«First»", "«Second»", "«Third»");
    }

    [Fact]
    public void Every_scalar_is_found_with_a_path_that_names_it()
    {
        var scalars = JsonSegmenter.Segment(Resource);

        scalars.Should().NotBeNull();
        scalars!.Select(s => s.KeyPath).Should().Contain(
        [
            "app.title",
            "actions.save",
            "status.synced",
            "status.retries",
            "status.tags.[0]",
            "status.tags.[1]",
        ]);
    }
}
