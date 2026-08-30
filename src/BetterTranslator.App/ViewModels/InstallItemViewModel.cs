using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One row in the install list, and the same row again as a progress row once
/// installing starts. Every figure comes from the transfer, not from a guess.
/// </summary>
public sealed partial class InstallItemViewModel(ModelComponent component) : ObservableObject
{
    public ModelComponent Component { get; } = component;

    public string Name => Component.Name;

    public string Summary => Component.Summary;

    /// <summary>
    /// Catalogue size, through the one byte formatter. Everything the row will
    /// fetch, companions included, so the figure beside a name is what ticking
    /// it actually costs.
    /// </summary>
    public string SizeLabel => ByteSize.Format(Component.InstallBytes);

    /// <summary>
    /// Where this artifact already is, if it is. A row that offers to download
    /// something the machine already has is the most expensive mistake this
    /// screen can make, so the answer is shown rather than found out later.
    /// </summary>
    /// <summary>
    /// Settable, not init-only: choosing a different models folder re-asks the
    /// question, and a row that kept its first answer would claim a file is
    /// present in a folder the reader has since navigated away from.
    /// </summary>
    [ObservableProperty]
    public partial Presence? Presence { get; set; }

    /// <summary>
    /// In the folder this install would write to. Nothing to do, so the box is
    /// ticked and locked: there is no meaningful "off" for a file that is
    /// already sitting at the destination.
    /// </summary>
    public bool IsInstalledHere => Presence?.Reason == PresenceReason.InstalledHere;

    /// <summary>
    /// Somewhere else on the machine. The box is free, and unticked by default,
    /// because downloading it into the chosen folder is a real choice: the copy
    /// that exists belongs to another folder, possibly another tool.
    /// </summary>
    public bool IsElsewhere => Presence?.Reason == PresenceReason.InstalledElsewhere;

    /// <summary>
    /// The mark says "a copy of this already exists", wherever it is, and the
    /// tooltip says where. The ticked-and-locked state is narrower: that belongs
    /// to the destination folder alone, because only a file already sitting
    /// there needs nothing done to it.
    /// </summary>
    public bool IsAlreadyHere => IsInstalledHere || IsElsewhere;

    /// <summary>
    /// The artifact is here and cannot load, because something it needs beside
    /// it is not. Deliberately not <see cref="IsAlreadyHere"/>: the row must
    /// stay tickable so the install can be completed from the screen that shows
    /// the problem.
    /// </summary>
    public bool IsIncomplete => Presence?.Reason == PresenceReason.MissingDependencies;

    public string? PresenceNote =>
        IsAlreadyHere || IsIncomplete ? Presence!.Explain(Name) : null;

    public bool HasPresenceNote => PresenceNote is not null;

    partial void OnPresenceChanged(Presence? value)
    {
        OnPropertyChanged(nameof(IsInstalledHere));
        OnPropertyChanged(nameof(IsElsewhere));
        OnPropertyChanged(nameof(IsAlreadyHere));
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(PresenceNote));
        OnPropertyChanged(nameof(HasPresenceNote));
        OnPropertyChanged(nameof(CanDeselect));
    }

    /// <summary>
    /// Ticked only when it is at the destination. A copy in another folder
    /// leaves the box off: choosing a new folder is asking for the file to be
    /// there, and the mark beside it says a copy already exists elsewhere.
    ///
    /// Never for a flavour this machine cannot run. No such row is PreSelected
    /// today, so this guards a rule rather than a case -- but the rule must not
    /// depend on that staying true.
    ///
    /// Asked for rather than fired from the presence setter, because presence is
    /// re-read at the moment the queue reads it and a tick the reader chose must
    /// survive that.
    /// </summary>
    public void SelectByDefault() =>
        IsSelected = !IsBlocked && (IsInstalledHere || (Component.PreSelected && !IsElsewhere));

    /// <summary>The runtime is tagged required and cannot be unticked.</summary>
    public bool IsRequired => Component.IsRequired;

    public bool IsCustom => Component.IsCustom;

    /// <summary>
    /// This machine cannot run it. Shown rather than hidden: the row says the
    /// flavour exists and why it is unavailable, which is what the settings card
    /// has always done. It cannot be ticked, so nothing can be spent on a
    /// library that will never load here.
    /// </summary>
    public bool IsBlocked => !Component.IsSupported;

    public string? BlockedReason => Component.UnsupportedReason;

    public bool HasBlockedReason => BlockedReason is not null;

    /// <summary>
    /// Locked once the file is at the destination: neither ticking nor unticking
    /// it would change anything, and a box that answers a click by doing nothing
    /// reads as broken. Locked too where the machine cannot run it, for the same
    /// reason from the other direction -- there is no useful "on".
    /// </summary>
    public bool CanDeselect => !Component.IsRequired && !IsInstalledHere && !IsBlocked;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial DownloadState State { get; set; } = DownloadState.Waiting;

    [ObservableProperty]
    public partial int Percent { get; set; }

    [ObservableProperty]
    public partial double BytesPerSecond { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Remaining { get; set; }

    [ObservableProperty]
    public partial string? Failure { get; set; }

    public bool IsInstalled => State == DownloadState.Installed;

    public bool IsActive => State == DownloadState.Downloading;

    /// <summary>
    /// "Waiting", "89% - 42.6 MB/s", or "Installed". The rate shows only on the
    /// row that is actually moving.
    /// </summary>
    public string StatusLabel => State switch
    {
        DownloadState.Installed => "Installed",
        DownloadState.Failed => Failure ?? "Failed",
        DownloadState.Cancelled => "Cancelled",
        DownloadState.Downloading when BytesPerSecond > 0 =>
            $"{Percent}% - {ByteSize.FormatRate(BytesPerSecond)}",
        DownloadState.Downloading => $"{Percent}%",
        _ => "Waiting",
    };

    /// <summary>Required is a tag beside the name, not a hidden rule.</summary>
    public string? Tag => Component.IsCustom ? "added by you" : IsRequired ? "required" : null;

    public bool HasTag => Tag is not null;

    public void Apply(DownloadProgress report)
    {
        State = report.State;
        Percent = report.Percent;
        BytesPerSecond = report.BytesPerSecond;
        Remaining = report.Remaining;
        Failure = report.Failure;
    }

    partial void OnStateChanged(DownloadState value) => RaiseStatus();

    partial void OnPercentChanged(int value) => RaiseStatus();

    partial void OnBytesPerSecondChanged(double value) => RaiseStatus();

    partial void OnIsSelectedChanged(bool value)
    {
        // Checked before required, and deliberately: a component that cannot run
        // here must never end up selected, however it got ticked. The two cannot
        // both be true today -- no runtime flavour is tagged required any more --
        // and if they ever were, refusing to install something unusable is the
        // safe way round.
        if (value && IsBlocked)
        {
            IsSelected = false;
            return;
        }

        // The runtime is required, so it cannot be turned off.
        if (!value && IsRequired)
        {
            IsSelected = true;
        }
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsActive));
    }
}
