using System.IO;
using System.Linq;
using System.Net.Http;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What the installer will and will not let you install. Offline: nothing here
/// opens a socket, it only asks the view model what it would allow.
/// </summary>
public sealed class InstallerSelectionTests
{
    private static FirstRunViewModel Installer()
    {
        var root = Path.Combine(Path.GetTempPath(), "bt-inst-" + Guid.NewGuid().ToString("N"));
        var paths = new InstallPaths(new AppPaths(root));
        paths.EnsureCreated();

        return new FirstRunViewModel(paths, new HttpClient());
    }

    [Fact]
    public void ARuntimeOnItsOwnCanBeInstalled()
    {
        // This used to be refused, on the reasoning that an app with no model
        // cannot translate. True, but it made the transfer path impossible to
        // exercise without committing to gigabytes of weights.
        var installer = Installer();

        foreach (var item in installer.Items)
        {
            item.IsSelected = item.Component.Kind == ComponentKind.Runtime;
        }

        installer.CanInstall.Should().BeTrue("a runtime alone is a legitimate thing to install");
        installer.InstallBlockedReason.Should().BeNull();
    }

    [Fact]
    public void TheButtonSaysContinueWhenThereIsNothingToFetch()
    {
        var installer = Installer();

        // Only things already on the machine: the press carries on rather than
        // transferring anything, and a button reading "Install 0 B" would name a
        // quantity of nothing.
        foreach (var item in installer.Items)
        {
            item.IsSelected = item.IsInstalledHere;
        }

        if (installer.SelectedBytes == 0 && installer.CanInstall)
        {
            installer.InstallLabel.Should().Be("Continue");
            installer.DownloadSizeLabel.Should().BeEmpty("no line should claim 0 B");
        }

        // Something that is genuinely missing prices itself.
        var missing = installer.Items.FirstOrDefault(i => !i.IsInstalledHere && i.Component.DownloadUrl is not null);

        if (missing is not null)
        {
            missing.IsSelected = true;
            installer.SelectedBytes.Should().BeGreaterThan(0);
            installer.InstallLabel.Should().StartWith("Install ");
            installer.DownloadSizeLabel.Should().EndWith("to download");
        }
    }

    [Fact]
    public void PuttingThePanelAwayIsNotFinishingWithIt()
    {
        // The regression this exists for: Dismiss used to raise Finished, so the
        // shell released the view model while its transfer was still running.
        // Reopening from the footer then built a second one with its own
        // DownloadManager, which raced the first for the same .part file and
        // came out as an IO error.
        var installer = Installer();

        var finished = 0;
        var dismissed = 0;

        installer.Finished += _ => finished++;
        installer.Dismissed += () => dismissed++;

        installer.DismissCommand.Execute(null);

        dismissed.Should().Be(1);
        finished.Should().Be(0, "a dismissal must not tell the shell the installer is done with");
    }

    [Fact]
    public void ADismissedInstallerStillReportsThatItIsTransferring()
    {
        // What the shell reads to decide whether to keep the instance. If this
        // stopped being true while a download ran, the instance would be dropped
        // on dismissal and the bug would be back.
        var installer = Installer();

        installer.IsInstalling.Should().BeFalse();

        installer.Stage = FirstRunStage.Installing;
        installer.DismissCommand.Execute(null);

        installer.IsInstalling.Should().BeTrue("dismissing hides the panel; it does not cancel the transfer");
    }

    [Fact]
    public void ASecondInstallIsRefusedWhileOneIsRunning()
    {
        // Nothing is ticked that would need fetching, so this cannot reach the
        // network either way -- what is being checked is that the guard returns
        // before the stage and the cancellation source are replaced.
        var installer = Installer();

        foreach (var item in installer.Items)
        {
            item.IsSelected = item.IsInstalledHere;
        }

        installer.Stage = FirstRunStage.Installing;
        installer.InstallCommand.Execute(null);

        installer.Stage.Should().Be(FirstRunStage.Installing,
            "a re-entrant install must not restart the run in progress");
    }

    [Fact]
    public void PickingNothingIsStillRefused()
    {
        var installer = Installer();

        foreach (var item in installer.Items)
        {
            item.IsSelected = false;
        }

        installer.CanInstall.Should().BeFalse();
        installer.InstallBlockedReason.Should().Contain("Pick something");
    }

    [Fact]
    public void TheNoModelNoteFollowsWhetherAModelWillBeThereAfterwards()
    {
        var installer = Installer();

        foreach (var item in installer.Items)
        {
            item.IsSelected = item.Component.Kind == ComponentKind.Runtime;
        }

        // Deliberately not asserted as "always warns": whether a model is
        // already on this machine is a property of the machine, and a test that
        // demanded the note would pass or fail depending on the developer's
        // model folder. The invariant is the implication, checked both ways.
        var aModelWillBeThere = installer.Items
            .Any(i => i.Component.Kind == ComponentKind.Model && (i.IsSelected || i.IsAlreadyHere));

        if (aModelWillBeThere)
        {
            installer.NoModelNote.Should().BeNull("something will be able to translate");
        }
        else
        {
            installer.NoModelNote.Should().Contain("nothing will translate");
        }

        // Selecting one always settles it, whatever the machine had.
        installer.Items.First(i => i.Component.Kind == ComponentKind.Model).IsSelected = true;
        installer.NoModelNote.Should().BeNull();
    }
}
