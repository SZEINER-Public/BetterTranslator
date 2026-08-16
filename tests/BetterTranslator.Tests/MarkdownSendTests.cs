using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Sending a Markdown message through the chat, rather than through the engine
/// directly. What is asserted here is the routing: that a Markdown message goes
/// block by block, that an ordinary one still goes as one piece, and that what
/// lands in the database is the same document in another language.
/// </summary>
public sealed class MarkdownSendTests : IAsyncLifetime
{
    private const string Markdown =
        "## Release notes\n\nKeeps your workspace **in sync**.\n\n- Windows 11\n- macOS 14\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-md", Guid.NewGuid().ToString("N"));
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

        // Marks what the model was given. Guillemets cannot occur anywhere else,
        // so stripping them from the result has to give the source back.
        _workspace.Translate = (ask, _) =>
        {
            _sent.Add(ask.Text);
            return Task.FromResult(new TranslationOutcome("«" + ask.Text + "»", 3, TimeSpan.FromMilliseconds(40)));
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
    public async Task A_Markdown_message_goes_block_by_block_and_keeps_its_structure()
    {
        await SendAsync(Markdown);

        // Heading, paragraph, two list items. One call each, and no call ever
        // carries a `#`, a `-` or an asterisk.
        _sent.Should().HaveCount(4);
        _sent.Should().NotContain(text => text.Contains('#') || text.StartsWith('-'));

        var entry = _workspace.Entries.Single();

        entry.IsMarkdown.Should().BeTrue();

        // Trimmed, because the composer trims what it is given before anything
        // else sees it. Everything inside the document is untouched.
        entry.Result.Replace("«", string.Empty).Replace("»", string.Empty).Should().Be(Markdown.Trim());
        entry.Result.Should().Contain("## «Release notes»", "the heading's text moved and its marker did not");

        // One pair of marks around the whole sentence, with the bold put back
        // inside it. Two pairs would mean the bold word had been sent as a call
        // of its own and the sentence translated in pieces.
        entry.Result.Should().Contain("«Keeps your workspace **in sync**.»");
    }

    [Fact]
    public async Task An_ordinary_message_still_goes_as_one_piece()
    {
        // The plain path is untouched: one call, the whole message, no
        // segmentation between the composer and the model.
        const string Prose = "The build failed on the second stage because the cache was cold.";

        await SendAsync(Prose);

        _sent.Should().ContainSingle().Which.Should().Be(Prose);
        _workspace.Entries.Single().IsMarkdown.Should().BeFalse();
    }

    [Fact]
    public async Task The_stored_row_is_the_translated_Markdown_and_reloads_as_such()
    {
        await SendAsync(Markdown);

        var chatId = _workspace.Rows.Single().Id;
        var translated = _workspace.Entries.Single().Result;

        // Reopening the chat is a fresh parse from the database, which is where
        // Copy and Source mode read from on every later session.
        _workspace.SelectedRow = null;
        _workspace.SelectedRow = _workspace.Rows.Single(r => r.Id == chatId);

        await _workspace.EntriesLoaded;

        var reloaded = _workspace.Entries.Single();
        reloaded.Result.Should().Be(translated);
        reloaded.IsMarkdown.Should().BeTrue();
        reloaded.Source.Should().Be(Markdown.Trim());
    }

    /// <summary>
    /// A brief wrapped in angle-bracket tags is raw HTML to CommonMark. Routing
    /// it to the Markdown path reported "Could not translate" for a message made
    /// almost entirely of prose, without ever sending it anywhere; and calling it
    /// Markdown at all gave it a View/Source switch whose View was blank.
    ///
    /// It is prose. It takes the ordinary path, a line at a time.
    /// </summary>
    [Fact]
    public async Task A_brief_wrapped_in_angle_bracket_tags_is_prose_and_is_translated()
    {
        const string Brief =
            "<context>\nPROJECT: BetterTranslator, a desktop translation tool.\nSTACK: C# WPF on .NET 10.\n</context>";

        await SendAsync(Brief);

        var entry = _workspace.Entries.Single();

        entry.IsMarkdown.Should().BeFalse("there is nothing here a renderer could show differently");
        entry.HasFormatToggle.Should().BeFalse();
        entry.Phase.Should().Be(TranslationPhase.Complete);

        _sent.Should().HaveCount(4, "one call per line, so no line can be lost inside a bigger one");
        _sent.Should().Contain("<context>").And.Contain("STACK: C# WPF on .NET 10.");

        foreach (var line in entry.Result.Split('\n'))
        {
            line.Should().Contain("«", "every line came back translated");
        }
    }

    [Fact]
    public async Task A_message_that_is_nothing_but_a_fenced_block_is_not_reported_as_a_failure()
    {
        const string Fence = "```json\n{\n  \"app.title\": \"Bubble Desktop\"\n}\n```";

        await SendAsync(Fence);

        _workspace.Entries.Single().Phase.Should().NotBe(TranslationPhase.Failed);
    }

    [Fact]
    public async Task A_fenced_block_is_never_offered_to_the_model()
    {
        await SendAsync("Run it:\n\n```bash\nbubble sync --watch\n```\n");

        _sent.Should().ContainSingle().Which.Should().Be("Run it:");
        _workspace.Entries.Single().Result.Should().Contain("```bash\nbubble sync --watch\n```");
    }
}
