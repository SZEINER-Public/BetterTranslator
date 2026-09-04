using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Core.Verification;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Whether the verifier's answer reaches the entry that renders it.
///
/// It used to not. The composer sends one unit at a time and the units are
/// spliced back into one answer, so a verification attached to a unit was
/// dropped with the unit and the entry was handed null on every send. These
/// tests pin the pair the verifier is asked about and the fact that it lands.
/// </summary>
public sealed class VerificationSurfaceTests : IAsyncLifetime
{
    private const string TwoLines = "The build failed.\nThe cache was cold.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-verify-surface", Guid.NewGuid().ToString("N"));
    private readonly List<(string Source, string Result)> _asked = [];

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
            Task.FromResult(new TranslationOutcome("«" + ask.Text + "»", 3, TimeSpan.FromMilliseconds(40)));

        _workspace.Verify = (source, content, _) =>
        {
            _asked.Add((source, content.Text!));
            return Flagged(content.Text!);
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

    private static VerificationResult Flagged(string target) => new()
    {
        Executed = true,
        Spans =
        [
            new VerificationSpan
            {
                Start = 0,
                Length = Math.Min(4, target.Length),
                Word = target[..Math.Min(4, target.Length)],
                Score = 20,
                Tier = SeverityTier.Error,
            },
        ],
    };

    private static VerificationResult Clean() => new() { Executed = true, Spans = [] };

    private async Task SendAsync(string text)
    {
        _workspace.Draft = text;
        await _workspace.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task The_verifier_is_asked_once_about_the_assembled_answer()
    {
        await SendAsync(TwoLines);

        // Two lines are two model calls, and one verification: the spans are
        // offsets into the text the entry renders, which only exists once the
        // lines have been spliced back together.
        var (source, result) = _asked.Should().ContainSingle().Subject;

        source.Should().Be(TwoLines, "the pair is the whole message, not one of its lines");
        result.Should().Be(_workspace.Entries.Single().Result);
    }

    [Fact]
    public async Task A_flagged_answer_reaches_the_entry_and_raises_the_underlined_surface()
    {
        await SendAsync("The build failed.");

        var entry = _workspace.Entries.Single();

        entry.Verification.Should().NotBeNull("the entry renders what the verifier said");
        entry.Verification!.HasFindings.Should().BeTrue();
        entry.ShowsVerifiedResult.Should().BeTrue("which is the underlined text block");
        entry.ShowsPlainResult.Should().BeFalse();
    }

    [Fact]
    public async Task A_clean_answer_still_renders_through_the_plain_box()
    {
        _workspace.Verify = (_, _, _) => Clean();

        await SendAsync("The build failed.");

        var entry = _workspace.Entries.Single();

        entry.Verification.Should().NotBeNull();
        entry.ShowsVerifiedResult.Should().BeFalse("there is nothing to underline");
        entry.ShowsPlainResult.Should().BeTrue();
    }

    [Fact]
    public async Task No_verifier_configured_leaves_the_entry_unmarked()
    {
        // The state on any machine without a dictionary: the factory yields
        // nothing, so the delegate is never set and the send is unaffected.
        _workspace.Verify = null;

        await SendAsync("The build failed.");

        var entry = _workspace.Entries.Single();

        entry.Verification.Should().BeNull();
        entry.ShowsVerifiedResult.Should().BeFalse();
        entry.ShowsPlainResult.Should().BeTrue("a translation with no verifier is still a translation");
    }

    [Fact]
    public async Task A_refused_send_is_never_verified()
    {
        _workspace.Translate = (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.FromMilliseconds(10)));

        await SendAsync("The build failed.");

        _asked.Should().BeEmpty("there is no answer to measure against the source");
        _workspace.Entries.Single().Verification.Should().BeNull();
    }

    [Fact]
    public async Task A_message_with_its_own_markup_keeps_the_format_toggle_instead()
    {
        // Recorded rather than fixed: an entry that can be shown rendered owns
        // its own view, and the underlines have nowhere to go in it. Verification
        // still runs and still lands, so the decision stays in one property.
        await SendAsync("## Release notes\n\nThe build failed.\n");

        var entry = _workspace.Entries.Single();

        entry.IsMarkdown.Should().BeTrue();
        entry.Verification.Should().NotBeNull();
        entry.ShowsVerifiedResult.Should().BeFalse("the rendered and source switch occupies that slot");
    }
}
