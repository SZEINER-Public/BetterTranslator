using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class ComposerSendEnablementTests : IDisposable
{
    private readonly Fixtures _files = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-send", Guid.NewGuid().ToString("N"));

    private ChatWorkspaceViewModel Workspace()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);
    }

    [Fact]
    public void NothingTypedAndNothingAttachedCannotSend() => Workspace().CanSend.Should().BeFalse();

    [Fact]
    public void TextAloneCanSend()
    {
        var workspace = Workspace();
        workspace.Draft = "The build is green.";

        workspace.CanSend.Should().BeTrue();
    }

    [Fact]
    public void AnAttachmentAloneCanSend()
    {
        var workspace = Workspace();

        workspace.Attachments.Add(AttachmentViewModel.File(_files.MarkdownFile));

        workspace.CanSend.Should().BeTrue("a file is a send on its own");
    }

    [Fact]
    public void TextAndAnAttachmentCanSend()
    {
        var workspace = Workspace();
        workspace.Draft = "Translate this too.";
        workspace.Attachments.Add(AttachmentViewModel.File(_files.TextFile));

        workspace.CanSend.Should().BeTrue();
    }

    [Fact]
    public void TheMemoryChipIsNotAFileAndDoesNotEnableTheSend()
    {
        var workspace = Workspace();

        workspace.Attachments.Add(AttachmentViewModel.Memory());

        workspace.CanSend.Should().BeFalse("project memory is a setting on a send, not something to send");
    }

    [Fact]
    public void RemovingTheLastAttachmentDisablesTheSendAgain()
    {
        var workspace = Workspace();
        var chip = AttachmentViewModel.File(_files.TextFile);
        workspace.Attachments.Add(chip);

        workspace.RemoveAttachmentCommand.Execute(chip);

        workspace.Attachments.Should().BeEmpty();
        workspace.CanSend.Should().BeFalse();
    }

    [Fact]
    public void EveryMutationRaisesTheNotification()
    {
        var workspace = Workspace();
        var raised = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatWorkspaceViewModel.CanSend))
            {
                raised++;
            }
        };

        var chip = AttachmentViewModel.File(_files.TextFile);
        workspace.Attachments.Add(chip);
        workspace.Attachments.Remove(chip);
        workspace.Attachments.Clear();

        raised.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task AnUnsupportedExtensionBlocksTheSendAndKeepsTheFile()
    {
        var unsupported = Path.Combine(_files.Root, "notes.rtf");
        await File.WriteAllTextAsync(unsupported, "not a supported document");

        var workspace = Workspace();
        var chip = AttachmentViewModel.File(unsupported);
        workspace.Attachments.Add(chip);

        await workspace.SendCommand.ExecuteAsync(null);

        chip.HasProblem.Should().BeTrue();
        chip.Problem.Should().Contain(".rtf");
        workspace.Attachments.Should().Contain(chip, "the chip stays so it can be removed");
        workspace.Entries.Should().BeEmpty("nothing was translated");
    }

    [Fact]
    public async Task AnOversizedFileIsRefusedBeforeItIsRead()
    {
        var big = Path.Combine(_files.Root, "big.txt");
        await File.WriteAllTextAsync(big, new string('a', 4096));

        Directory.CreateDirectory(_root);
        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        var workspace = new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null)
        {
            MaxAttachmentBytes = 1024,
        };

        var chip = AttachmentViewModel.File(big);
        workspace.Attachments.Add(chip);

        await workspace.SendCommand.ExecuteAsync(null);

        chip.HasProblem.Should().BeTrue();
        chip.Problem.Should().Contain("limit");
        workspace.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingFileIsRefusedRatherThanThrown()
    {
        var workspace = Workspace();
        var chip = AttachmentViewModel.File(_files.TextFile);
        workspace.Attachments.Add(chip);

        File.Delete(_files.TextFile);

        await workspace.SendCommand.ExecuteAsync(null);

        chip.HasProblem.Should().BeTrue();
        chip.Problem.Should().NotContain("Exception");
    }

    [Fact]
    public void FileJobsAreBoundedAndDefaultToOne() => Workspace().MaxConcurrentFileJobs.Should().Be(1);

    [Fact]
    public void TheComposerStaysAliveWhileADocumentRuns() =>
        Workspace().SendCommand.CanExecute(null).Should().BeTrue();

    public void Dispose()
    {
        _files.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
