using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Json;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A JSON message in the transcript, and the routing that gets it there. The
/// existing Markdown and plain paths are asserted alongside, because the whole
/// risk of adding a third route is that it takes messages belonging to the other
/// two.
/// </summary>
public sealed class JsonMessageTests : IAsyncLifetime
{
    private const string Resource =
        "{\n  \"app\": {\n    \"title\": \"Bubble Desktop\"\n  },\n  \"actions\": {\n    \"save\": \"Save\"\n  },\n  \"retries\": 3\n}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-json", Guid.NewGuid().ToString("N"));
    private readonly List<string> _sent = [];

    private ChatWorkspaceViewModel _workspace = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        _workspace = new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null)
        {
            Language = TargetLanguage.All.Single(l => l.Name == "Czech"),
        };

        _workspace.Translate = (ask, _) =>
        {
            _sent.Add(ask.Text);

            // Echoes each marker and wraps what follows it, which is what a
            // model that did as it was asked would produce.
            var answer = string.Join('\n', ask.Text.Split('\n').Select(line =>
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                return line.StartsWith("[[", StringComparison.Ordinal) && space > 0
                    ? line[..space] + " «" + line[(space + 1)..] + "»"
                    : "«" + line + "»";
            }));

            return Task.FromResult(new TranslationOutcome(answer, 3, TimeSpan.FromMilliseconds(40)));
        };
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail an otherwise green run.
        }

        return Task.CompletedTask;
    }

    private async Task SendAsync(string text)
    {
        _workspace.Draft = text;
        await _workspace.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_resource_file_comes_back_with_only_its_values_translated()
    {
        await SendAsync(Resource);

        var entry = _workspace.Entries.Single();

        entry.IsJson.Should().BeTrue();
        entry.IsMarkdown.Should().BeFalse("a resource file is not a Markdown document");
        entry.Phase.Should().Be(TranslationPhase.Complete);

        entry.Result.Replace("«", string.Empty).Replace("»", string.Empty)
            .Should().Be(Resource, "nothing outside a string value may have moved");

        using var document = JsonDocument.Parse(entry.Result);
        document.RootElement.GetProperty("retries").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("app").GetProperty("title").GetString().Should().Be("«Bubble Desktop»");
    }

    [Fact]
    public async Task Keys_never_reach_the_model()
    {
        await SendAsync(Resource);

        var everything = string.Join('\n', _sent);

        everything.Should().NotContain("app")
            .And.NotContain("actions")
            .And.NotContain("retries")
            .And.NotContain("{").And.NotContain("}");
    }

    [Fact]
    public async Task Values_are_batched_rather_than_sent_one_call_each()
    {
        await SendAsync(Resource);

        // Two translatable values, one request.
        _sent.Should().ContainSingle();
        _sent[0].Should().Contain("Bubble Desktop").And.Contain("Save");
    }

    [Fact]
    public async Task The_copy_control_yields_JSON_that_reparses()
    {
        await SendAsync(Resource);

        // Copy puts Result on the clipboard and Source mode shows the same
        // string, so this is what both of them hand over.
        var raw = _workspace.Entries.Single().Result;

        var parse = () => JsonDocument.Parse(raw);
        parse.Should().NotThrow();
    }

    [Fact]
    public async Task The_message_opens_as_keys_against_values_and_the_switch_shows_the_raw_file()
    {
        await SendAsync(Resource);

        var entry = _workspace.Entries.Single();

        entry.HasFormatToggle.Should().BeTrue();
        entry.ShowsSourceJson.Should().BeTrue();
        entry.ShowsResultJson.Should().BeTrue();
        entry.ShowsSourceSyntax.Should().BeFalse();

        entry.SourceRows.Select(r => r.KeyPath).Should().Equal("app.title", "actions.save", "retries");
        entry.SourceRows.Single(r => r.KeyPath == "retries").IsString.Should().BeFalse();

        entry.ToggleFormatCommand.Execute(null);

        entry.ShowsSourceSyntax.Should().BeTrue();
        entry.ShowsResultSyntax.Should().BeTrue();
        entry.ShowsSourceJson.Should().BeFalse();
        entry.ShowsResultJson.Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_JSON_behaves_exactly_as_it_did_before()
    {
        // A brace and then prose. It is not a document, so it takes the path it
        // would have taken with none of this present: one call, whole, no
        // segmentation.
        const string NotJson = "{ this is not a document, just a sentence with a brace";

        await SendAsync(NotJson);

        _sent.Should().ContainSingle().Which.Should().Be(NotJson);
        _workspace.Entries.Single().IsJson.Should().BeFalse();
    }

    [Fact]
    public async Task A_Markdown_message_still_takes_the_Markdown_path()
    {
        await SendAsync("## Release notes\n\nKeeps your workspace in sync.");

        var entry = _workspace.Entries.Single();

        entry.IsJson.Should().BeFalse();
        entry.IsMarkdown.Should().BeTrue();
        _sent.Should().HaveCount(2, "a heading and a paragraph, one call each");
    }

    [Fact]
    public async Task An_ordinary_message_still_goes_as_one_piece()
    {
        const string Prose = "The build failed on the second stage.";

        await SendAsync(Prose);

        _sent.Should().ContainSingle().Which.Should().Be(Prose);

        var entry = _workspace.Entries.Single();
        entry.IsJson.Should().BeFalse();
        entry.HasFormatToggle.Should().BeFalse("a plain message has no second way of being read");
    }

    [Fact]
    public async Task A_document_of_numbers_alone_is_not_routed_to_the_JSON_path()
    {
        // Parses, holds no language. Sending it would spend calls to be told so;
        // reporting a failure for it would be worse.
        const string Numbers = """{"width":800,"height":600,"ratio":1.33}""";

        JsonSyntax.LooksLikeJson(Numbers).Should().BeTrue();
        JsonSyntax.HasTranslatableValues(Numbers).Should().BeFalse();

        await SendAsync(Numbers);

        _sent.Should().ContainSingle().Which.Should().Be(Numbers, "it fell through to the ordinary path");
    }
}
