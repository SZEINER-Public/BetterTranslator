using System.Collections.ObjectModel;
using System.Net.Http;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BetterTranslator.App.ViewModels;

public enum FirstRunStage
{
    ChooseWhatToInstall,
    Installing,

    /// <summary>
    /// Where to start. Reached straight from Installing: a screen whose only
    /// content was the word "Ready" and a Continue button asked for a click to
    /// learn nothing, and the choice underneath it is what the reader came for.
    /// </summary>
    ChooseWhereToStart,
}

public enum StartChoice
{
    JustChat,
    Repository,
    Folder,
}

/// <summary>
/// S14. Opens over the chat on a fresh install and does not close until the
/// runtime and at least one model are present, which is what keeps every route
/// needing a model blocked until then.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly ComponentCatalog _catalog = new();
    private readonly InstallPaths _paths;
    private readonly ComponentInstallQueue _queue;
    private CancellationTokenSource? _installing;

    public FirstRunViewModel(InstallPaths paths, HttpClient httpClient)
    {
        _paths = paths;

        // The resolver runs before any socket, so the list already knows what is
        // on the machine. Without it this screen offers to fetch gigabytes that
        // are sitting in another tool's model folder.
        var resolver = new ModelResolver(paths, new BetterTranslator.Runtime.Inference.ModelLibrary());

        // Everything goes through the queue: what a row displays and what the
        // transfer decides are the same answer, asked once.
        _queue = new ComponentInstallQueue(new DownloadManager(httpClient, paths, resolver), resolver);

        // Runtime rows come from the hardware probe, so a Radeon is never offered
        // a CUDA build it could not load.
        var hardware = BackendCatalog.Probe();
        HardwareLabel = hardware.VendorLabel;

        foreach (var component in ComponentCatalog.RuntimesFor(hardware).Concat(_catalog.All))
        {
            Items.Add(new InstallItemViewModel(component));
        }

        RefreshPresence();

        foreach (var item in Items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(InstallItemViewModel.IsSelected))
                {
                    RaiseSelectionFigures();
                }
            };
        }
    }

    public ObservableCollection<InstallItemViewModel> Items { get; } = [];

    /// <summary>
    /// Re-asks every row where its artifact is. Called whenever the models
    /// folder changes, because "already installed" is a question about a folder
    /// and the answer stops being true the moment a different one is chosen.
    ///
    /// A file found somewhere else on the machine stays satisfied and is read
    /// where it lies -- nothing is copied or moved into the new folder. Moving
    /// gigabytes because a path changed would be slow, would need free space at
    /// both ends, and would leave the other tool that owns the file looking at a
    /// gap. Only an artifact that exists nowhere becomes a real download.
    /// </summary>
    public void RefreshPresence()
    {
        foreach (var item in Items)
        {
            item.Presence = _queue.Resolve(item.Component);
            item.SelectByDefault();
        }

        RaiseSelectionFigures();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NoModelNote));
    }

    [ObservableProperty]
    public partial FirstRunStage Stage { get; set; } = FirstRunStage.ChooseWhatToInstall;

    [ObservableProperty]
    public partial string CustomModelLink { get; set; } = string.Empty;

    /// <summary>Why a pasted link was refused, naming the thing that was wrong.</summary>
    [ObservableProperty]
    public partial string? LinkRejection { get; set; }

    [ObservableProperty]
    public partial string FooterStatus { get; set; } = string.Empty;

    public bool IsChoosing => Stage == FirstRunStage.ChooseWhatToInstall;

    public bool IsInstalling => Stage == FirstRunStage.Installing;

    public bool IsChoosingStart => Stage == FirstRunStage.ChooseWhereToStart;

    public string Title => Stage switch
    {
        // The title states the situation, and the situation is not always
        // "nothing". Opened from the toolbar on a machine that already has
        // models, the old wording contradicted the ticked rows below it.
        FirstRunStage.ChooseWhatToInstall => Items.Any(i => i.IsInstalledHere)
            ? "Models and runtimes"
            : "Nothing is installed yet",
        FirstRunStage.Installing => "Installing",
        _ => "Where do you want to start?",
    };

    /// <summary>
    /// Puts the panel away. Deliberately not the same event as finishing: a
    /// dismissal used to raise Finished, which let the shell drop this view
    /// model while its transfer was still running, so reopening built a second
    /// one that raced the first for the same .part file.
    ///
    /// Hiding is all this does. Whatever is downloading carries on, and the
    /// shell decides whether the instance is still worth keeping.
    /// </summary>
    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke();

    /// <summary>Raised when the panel is put away without finishing.</summary>
    public event Action? Dismissed;

    public bool IsUsingDefaultFolder => _paths.IsDefault;

    public string DefaultPath => _paths.DefaultModelsFolder;

    /// <summary>The real resolved path, or the folder the dialog returned.</summary>
    public string ChosenPath => _paths.ModelsFolder;

    public string PathLabel => _paths.IsDefault ? "Default path" : "Chosen folder";

    private IEnumerable<InstallItemViewModel> Selected => Items.Where(i => i.IsSelected);

    /// <summary>What the probe found, named above the runtime rows.</summary>
    public string HardwareLabel { get; } = string.Empty;

    /// <summary>
    /// Whatever is ticked can be installed, including a runtime on its own.
    ///
    /// This used to demand at least one model, on the reasoning that an app with
    /// no model cannot translate. True, but it is not this screen's business to
    /// enforce: the screen installs what was asked for, and every route that
    /// needs a model already reaches the download manager when one is missing.
    /// Refusing a runtime-only install also made the transfer path impossible to
    /// exercise without committing to gigabytes of weights.
    /// </summary>
    public bool CanInstall => Selected.Any();

    /// <summary>
    /// What the transfer will actually cost. A ticked row that is already on the
    /// machine contributes nothing, so the figure answers "how much will be
    /// downloaded" rather than "how much is selected".
    /// </summary>
    public long SelectedBytes =>
        ComponentCatalog.TotalBytes(Selected.Where(i => !i.IsInstalledHere).Select(i => i.Component));

    /// <summary>Prices the real selection, for example "Install 4.1 GB".</summary>
    /// <summary>
    /// Prices the transfer when there is one, and says Continue when there is
    /// not. "Install 0 B" is a button that names a quantity of nothing: it reads
    /// as a bug, and it hides what the press actually does, which is carry on
    /// with what the machine already has.
    /// </summary>
    public string InstallLabel => SelectedBytes > 0 ? "Install " + ByteSize.Format(SelectedBytes) : "Continue";

    /// <summary>Empty when nothing will be fetched, so no line claims "0 B".</summary>
    public string DownloadSizeLabel => SelectedBytes > 0 ? ByteSize.Format(SelectedBytes) + " to download" : string.Empty;

    /// <summary>Stated when the action is blocked, rather than left to a grey button.</summary>
    public string? InstallBlockedReason => CanInstall
        ? null
        : "Pick something to install, or paste a link to a model.";

    /// <summary>
    /// Said once something is ticked but no model will be present afterwards.
    /// Not a block -- installing only a runtime is allowed and useful -- but the
    /// consequence is worth stating rather than discovering at the first
    /// translation.
    /// </summary>
    public string? NoModelNote =>
        CanInstall && !Items.Any(i => i.Component.Kind == ComponentKind.Model && (i.IsSelected || i.IsAlreadyHere))
            ? "No model is selected, so nothing will translate until you add one."
            : null;

    [RelayCommand]
    private void UseDefaultFolder()
    {
        _paths.UseDefault();
        RaisePathFigures();
        RefreshPresence();
    }

    /// <summary>
    /// Cancelling the folder dialog leaves the choice on Default folder and
    /// opens nothing else.
    /// </summary>
    [RelayCommand]
    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where models go", Multiselect = false };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _paths.UseFolder(dialog.FolderName);
        RaisePathFigures();
        RefreshPresence();
    }

    [RelayCommand]
    private void AddCustomModel()
    {
        var result = _catalog.AddCustom(CustomModelLink);

        if (!result.IsAccepted)
        {
            LinkRejection = result.Rejection;
            return;
        }

        LinkRejection = null;
        CustomModelLink = string.Empty;

        var added = _catalog.Custom[^1];
        var item = new InstallItemViewModel(added) { Presence = _queue.Resolve(added), IsSelected = true };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallItemViewModel.IsSelected))
            {
                RaiseSelectionFigures();
            }
        };

        Items.Add(item);
        RaiseSelectionFigures();
    }

    [RelayCommand]
    private void RemoveCustomModel(InstallItemViewModel item)
    {
        if (!item.Component.IsCustom)
        {
            return;
        }

        _catalog.RemoveCustom(item.Component.Id);
        Items.Remove(item);
        RaiseSelectionFigures();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        // IsInstalling as well as CanInstall: a second run would replace the
        // cancellation source out from under the first and put two writers on
        // one .part file. The progress screen hides the button, so this is the
        // backstop for any other route in.
        if (!CanInstall || IsInstalling)
        {
            return;
        }

        Stage = FirstRunStage.Installing;
        _paths.EnsureCreated();
        _installing = new CancellationTokenSource();

        // Installed state is settled here, once, from the queue's own answer,
        // and the row is repainted with it before anything is enqueued. That is
        // the whole defect: the tick said installed, the queue asked a second
        // time somewhere else, and a row displayed as present was fetched again.
        var queue = new List<InstallItemViewModel>();

        foreach (var item in Selected.ToList())
        {
            item.Presence = _queue.Resolve(item.Component);

            if (item.IsInstalledHere)
            {
                item.State = DownloadState.Installed;
                continue;
            }

            item.State = DownloadState.Waiting;
            queue.Add(item);
        }

        RaiseSelectionFigures();

        var installed = new List<string>();

        foreach (var item in queue)
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                item.Apply(report);
                FooterStatus = BuildFooter(item);
            });

            var result = await _queue
                .EnqueueAsync(item.Component, progress, _installing.Token)
                .ConfigureAwait(true);

            item.Apply(result);

            if (result.State == DownloadState.Cancelled)
            {
                Stage = FirstRunStage.ChooseWhatToInstall;
                return;
            }

            if (result.State == DownloadState.Installed)
            {
                installed.Add(item.Name);
            }
        }

        FooterStatus = string.Empty;
        Stage = FirstRunStage.ChooseWhereToStart;

        // One batch, one signal, and only when something was actually written.
        // A run that fetched nothing has changed nothing for the process to
        // pick up, so it must not spend the restart the guard allows.
        if (installed.Count > 0)
        {
            BatchInstalled?.Invoke(new InstallBatch(Guid.NewGuid().ToString("N")[..8], installed));
        }
    }

    /// <summary>Raised once the user has chosen where to start.</summary>
    public event Action<StartChoice>? Finished;

    /// <summary>
    /// Raised once per install run that actually wrote something, carrying the
    /// id the restart guard spends and the names to put in the notice.
    /// </summary>
    public event Action<InstallBatch>? BatchInstalled;

    [RelayCommand]
    private void Start(string choice) =>
        Finished?.Invoke(Enum.Parse<StartChoice>(choice, ignoreCase: true));

    private string BuildFooter(InstallItemViewModel active)
    {
        var parts = new List<string> { ChosenPath };

        if (active.BytesPerSecond > 0)
        {
            parts.Add(ByteSize.FormatRate(active.BytesPerSecond));
        }

        var left = TransferEstimate.FormatRemaining(active.Remaining);
        if (left.Length > 0)
        {
            parts.Add(left);
        }

        return string.Join(" - ", parts);
    }

    partial void OnStageChanged(FirstRunStage value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NoModelNote));
        OnPropertyChanged(nameof(IsChoosing));
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(IsChoosingStart));
    }

    private void RaiseSelectionFigures()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(DownloadSizeLabel));
        OnPropertyChanged(nameof(NoModelNote));
        OnPropertyChanged(nameof(InstallBlockedReason));
    }

    private void RaisePathFigures()
    {
        OnPropertyChanged(nameof(IsUsingDefaultFolder));
        OnPropertyChanged(nameof(ChosenPath));
        OnPropertyChanged(nameof(PathLabel));
    }
}
