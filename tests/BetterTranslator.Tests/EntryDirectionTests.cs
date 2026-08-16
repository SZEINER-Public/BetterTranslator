using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using BetterTranslator.App.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The pair a retry runs under.
///
/// It used to be assembled at the moment Retry was pressed: the target resolved
/// from the entry's stored English name, the source read straight off the
/// composer. Both pickers move between a send and a retry, and a name the
/// registry could not resolve fell back to whatever the composer was showing --
/// so a retry could translate into a language nobody chose, under the heading
/// the row already carried.
/// </summary>
public sealed class EntryDirectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-direction", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AnEntryIsRetriedUnderThePairItWasSentUnderWhicheverWayThePickersMoved()
    {
        var workspace = Workspace();

        var sent = new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = "The build is green.",
            CreatedAt = DateTimeOffset.UtcNow,
            TargetLanguage = "German",
            SourceCode = "en",
            TargetCode = "de",
        };

        workspace.Language = TargetLanguage.All.First(l => l.Code == "fr");
        workspace.SourceLanguage = TargetLanguage.All.First(l => l.Code == "es");

        var direction = workspace.DirectionFor(sent);

        direction.Target.Code.Value.Should().Be("de", "the row was sent for German");
        direction.Source.Code.Value.Should().Be("en", "and from English");
    }

    [Fact]
    public void ARowWithNoRecordedTargetFallsBackToItsNameRatherThanToTheComposer()
    {
        var workspace = Workspace();
        workspace.Language = TargetLanguage.All.First(l => l.Code == "fr");

        var legacy = Legacy("German");

        workspace.DirectionFor(legacy).Target.Code.Value
            .Should().Be("de", "a row written before the codes existed still names its language");
    }

    [Fact]
    public void ARowWhoseLanguageCannotBeResolvedIsUnknownRatherThanTheComposersLanguage()
    {
        var workspace = Workspace();
        workspace.Language = TargetLanguage.All.First(l => l.Code == "fr");

        var direction = workspace.DirectionFor(Legacy("Kryptonian"));

        direction.HasUnknownTarget.Should().BeTrue(
            "an unresolvable language is unknown; substituting the composer's would translate into a language nobody chose");
        direction.Target.Code.Value.Should().NotBe("fr");
    }

    [Fact]
    public void AnUnrecordedSourceStillComesFromTheComposerBecauseThoseRowsCarriedNone()
    {
        var workspace = Workspace();
        workspace.SourceLanguage = TargetLanguage.All.First(l => l.Code == "es");

        workspace.DirectionFor(Legacy("German")).Source.Code.Value
            .Should().Be("es", "there is nothing else a row without a source can be retried from");
    }

    [Fact]
    public async Task ARowWithNoLanguageToTranslateIntoIsRefusedRatherThanSentWithNone()
    {
        var workspace = Workspace();
        var asked = 0;

        workspace.Translate = (ask, _) =>
        {
            asked++;
            return Task.FromResult(new TranslationOutcome(ask.Text, 0, TimeSpan.Zero));
        };

        var view = new EntryViewModel(Legacy("Kryptonian"));

        await workspace.RetryEntryCommand.ExecuteAsync(view);

        asked.Should().Be(0, "there is no language to translate into, so nothing should reach the model");
        view.Phase.Should().Be(TranslationPhase.Failed);
        view.Note.Should().NotBeNullOrWhiteSpace("and the row says why");
    }

    [Fact]
    public async Task TheRecordedPairSurvivesAReload()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        var store = new ChatStore(database);

        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = "Direction",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.AddChatAsync(chat, CancellationToken.None);

        var entry = new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            Kind = EntryKind.Sentence,
            Source = "The build is green.",
            CreatedAt = DateTimeOffset.UtcNow,
            TargetLanguage = "Chinese (Simplified)",
            SourceCode = "en",
            TargetCode = "zh-CN",
        };

        await store.AddEntryAsync(entry, CancellationToken.None);

        var reloaded = await store.GetEntryAsync(entry.Id, CancellationToken.None);

        reloaded.Should().NotBeNull();
        reloaded!.SourceCode.Should().Be("en");
        reloaded.TargetCode.Should().Be("zh-CN", "the region matters: zh-CN and zh-TW are not the same target");
    }

    private static Entry Legacy(string targetName) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.Sentence,
        Source = "The build is green.",
        CreatedAt = DateTimeOffset.UtcNow,
        TargetLanguage = targetName,
    };

    private ChatWorkspaceViewModel Workspace()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);
    }
}
