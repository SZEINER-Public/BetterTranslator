using System.Collections.ObjectModel;
using System.IO;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public enum SettingsTab
{
    Memory,
    YourData,
    Runtime,
    Downloads,
    Agent,
    Config,
    Updates,
}

/// <summary>
/// One editable configuration file in the Config tab.
///
/// The engine ships these embedded, which makes them always present and
/// impossible to corrupt, but also impossible to edit. Saving here writes a copy
/// beside the database that takes precedence; resetting deletes it.
/// </summary>
public sealed partial class ConfigFileViewModel(BetterTranslator.Engine.Config.ConfigFile file, BetterTranslator.Engine.Config.ConfigStore store)
    : ObservableObject
{
    public BetterTranslator.Engine.Config.ConfigFile File { get; } = file;

    public string Name => File.DisplayName;

    public string Description => File.Description;

    public string Path => store.PathFor(File);

    /// <summary>True once a copy of the reader's own is in force.</summary>
    public bool IsEdited => store.IsEdited(File);

    [ObservableProperty]
    public partial string Text { get; set; } = store.Current(file);

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsValid { get; set; } = true;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>
    /// Checked as it is typed, so a missing brace is named while the caret is
    /// still near it rather than on the press of Save.
    /// </summary>
    partial void OnTextChanged(string value)
    {
        var verdict = BetterTranslator.Engine.Config.ConfigStore.Validate(File, value);

        IsValid = verdict.Accepted;
        Message = verdict.Detail;
    }

    [RelayCommand]
    private void Save()
    {
        var verdict = store.Save(File, Text);

        IsValid = verdict.Accepted;

        if (verdict.Accepted)
        {
            // The per-language configuration is cached so a rules file cannot
            // change mid-document. That same cache would hide a glossary saved
            // here until the app restarted, so it is dropped on a save.
            BetterTranslator.Engine.Slop.RagConfig.Forget();
        }

        Message = verdict.Accepted
            ? verdict.Detail + (File.Format == BetterTranslator.Engine.Config.ConfigFormat.Markdown
                ? " In force from the next send."
                : " Restart to load it.")
            : verdict.Detail;

        OnPropertyChanged(nameof(IsEdited));
    }

    [RelayCommand]
    private void Reset()
    {
        var verdict = store.Reset(File);

        Text = BetterTranslator.Engine.Config.ConfigStore.Shipped(File);
        IsValid = true;
        Message = verdict.Detail + " Restart to load it.";

        OnPropertyChanged(nameof(IsEdited));
    }

    /// <summary>
    /// Puts the shipped text in the editor without deleting the saved copy, so a
    /// change can be compared against the original before it is thrown away.
    /// </summary>
    [RelayCommand]
    private void ShowShipped()
    {
        Text = BetterTranslator.Engine.Config.ConfigStore.Shipped(File);
        Message = "Showing the shipped file. Save to make it yours, or switch away to discard.";
    }

    /// <summary>
    /// Hands the file to whatever the machine opens .json with.
    ///
    /// A panel inside Settings is a poor place to read a 19 KB registry: no
    /// search, no line numbers, no folding. Rather than build an editor, the
    /// file goes to the one the reader already has. A second window of our own
    /// is also not the answer -- C13 keeps dialogs as a layer inside MainWindow
    /// so the custom chrome stays behind them, and a full editor window would be
    /// the first thing to break that.
    ///
    /// The panel does not lock while it is open. Whatever comes back is read on
    /// Reload, and the file on disk is the one that counts either way.
    /// </summary>
    [RelayCommand]
    private void EditExternally()
    {
        // There has to be a file to hand over. Saving the current text first
        // also means the reader edits what they were looking at rather than the
        // shipped copy they had not chosen yet.
        if (!store.IsEdited(File))
        {
            var saved = store.Save(File, Text);

            if (!saved.Accepted)
            {
                IsValid = false;
                Message = "Fix this before opening it elsewhere: " + saved.Detail;
                return;
            }

            OnPropertyChanged(nameof(IsEdited));
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing is registered for .json on this machine. Notepad always is.
            try
            {
                System.Diagnostics.Process.Start("notepad.exe", $"\"{Path}\"");
            }
            catch (Exception fallback) when (fallback is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Message = $"Could not open an editor: {fallback.Message}";
                return;
            }
        }

        Message = "Opened in your text editor. Save there, then press Reload here.";
    }

    /// <summary>
    /// Reads the file back after it has been edited elsewhere.
    ///
    /// Raw, not resolved: a file that has just been broken outside the app is
    /// shown broken, with the parser's complaint under it. Falling back to the
    /// shipped text here would look like the edit had silently vanished.
    /// </summary>
    [RelayCommand]
    private void Reload()
    {
        var onDisk = store.ReadRaw(File);

        Text = onDisk ?? BetterTranslator.Engine.Config.ConfigStore.Shipped(File);

        // OnTextChanged has already validated and written a message; only say
        // where this came from when nothing was wrong with it.
        if (IsValid)
        {
            Message = onDisk is null
                ? "No saved copy, so this is the shipped file."
                : "Reloaded from disk. " + Message;
        }

        OnPropertyChanged(nameof(IsEdited));
    }
}

/// <summary>One row in the Downloads catalogue: everything this build supports.</summary>
public sealed record CatalogueRow(
    string Name, string Note, string SizeLabel, bool IsInstalled, bool CanFetch, bool IsSupported = true)
{
    /// <summary>
    /// Four states, not two. "Installed", "can be downloaded", "no link yet" and
    /// "will not run here" read differently, and collapsing any pair would say
    /// something untrue: offering a download for a flavour that has never been
    /// built, or calling one "Available" on a machine that cannot load it.
    ///
    /// Unsupported outranks fetchable, because it is the fact that decides
    /// whether fetching is worth anything. CUDA on a Radeon has a real link and
    /// a real size and still cannot run.
    /// </summary>
    public string StateLabel => (IsInstalled, IsSupported, CanFetch) switch
    {
        (true, _, _) => "Installed",
        (false, false, _) => "Cannot run here",
        (false, true, true) => "Available",
        _ => "Not built yet",
    };
}

/// <summary>One installed model in the Models on disk list.</summary>
public sealed record InstalledModelRow(string Id, string Name, string Note, string SizeLabel, bool CanDelete);

/// <summary>
/// One backend card on the Runtime tab.
///
/// <paramref name="catalogue"/> is the same flavour as the installer prices it.
/// The card cannot ask <c>ComponentCatalog</c> for it: that type already
/// references <c>BackendCatalog</c>, so the reverse would be a cycle. The join
/// happens in <see cref="SettingsViewModel.LoadRuntimeTab"/>, which sits above
/// both -- which also keeps one source of truth for the size rather than
/// spelling it a second time here.
/// </summary>
public sealed partial class BackendOptionViewModel(
    BackendOption option, ModelComponent? catalogue, Func<RuntimeBackend, bool> isChosen)
    : ObservableObject
{
    public BackendOption Option { get; } = option;

    public RuntimeBackend Backend => Option.Backend;

    public string Title => Option.Title;

    public string Summary => Option.Summary;

    public bool CanSelect => Option.CanSelect;

    public bool IsRecommended => Option.IsRecommended;

    public string? BlockedReason => Option.BlockedReason;

    public bool HasBlockedReason => BlockedReason is not null;

    public bool IsChosen => isChosen(Option.Backend);

    /// <summary>
    /// There is something to press. "Not downloaded yet" was a dead end while no
    /// GPU flavour could be fetched at all; it is an instruction now.
    ///
    /// All three conditions are load-bearing. Supported, so the button never
    /// appears on a card the hardware cannot run. Not installed, because there
    /// is nothing to get. And a real link, so a future flavour added without one
    /// offers nothing rather than failing after the press.
    /// </summary>
    public bool CanFetch => Option.IsSupported && !Option.IsInstalled && catalogue is { IsFetchable: true };

    /// <summary>
    /// Priced, because the size is the part worth knowing before pressing.
    /// InstallBytes rather than SizeBytes: CUDA moves 631 MB, not the 138 MB of
    /// its own DLL.
    /// </summary>
    public string FetchLabel =>
        catalogue is null ? "Get" : $"Get {ByteSize.Format(catalogue.InstallBytes)}";

    public void Refresh() => OnPropertyChanged(nameof(IsChosen));
}

/// <summary>One GGUF on the Runtime tab's model list.</summary>
public sealed partial class ModelOptionViewModel(LocalModel model, Func<string, bool> isChosen) : ObservableObject
{
    public LocalModel Model { get; } = model;

    public string Name => Model.Name;

    public string Publisher => Model.Publisher;

    public string SizeLabel => ByteSize.Format(Model.SizeBytes);

    public bool IsChosen => isChosen(Model.Path);

    public void Refresh() => OnPropertyChanged(nameof(IsChosen));
}

/// <summary>
/// S13. Memory, Your data and Downloads. Every size figure comes from the one
/// byte formatter, so a model reads identically here and in the download
/// manager.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly CacheInspector _cache;
    private readonly InstallPaths _paths;
    private readonly Action _close;
    private readonly Func<Task> _storedDataDeleted;
    private readonly Func<AppSettings, Task> _applied;

    /// <summary>
    /// Hands the download back to the shell rather than opening a second one
    /// here. The shell keeps a single installer instance for a reason: a second
    /// DownloadManager would race the first for the same .part file.
    ///
    /// Required, not optional with a no-op default. Defaulting it would let a
    /// construction site forget it and ship a Get button that answers a press by
    /// doing nothing, which is indistinguishable from a broken one.
    /// </summary>
    private readonly Action _openDownloads;

    private readonly BackendCatalog _backends = new();
    private readonly ModelLibrary _library = new();

    private AppSettings _settings = new();

    /// <summary>Whether the model showing here was chosen here. See <see cref="Write"/>.</summary>
    private bool _modelChosenHere;

    private Task _writing = Task.CompletedTask;

    /// <summary>
    /// The save in flight, for a test that needs to know the row has landed.
    /// Saving is fired from a property setter and finishes on its own, so there
    /// is otherwise nothing to wait on but a guess at how long it takes.
    /// </summary>
    internal Task Written => _writing;

    /// <summary>Whether a model chosen here is still waiting to be written.</summary>
    internal bool ModelChosenHere => _modelChosenHere;
    private bool _loading;

    public SettingsViewModel(
        SettingsStore store,
        CacheInspector cache,
        InstallPaths paths,
        Func<AppSettings, Task> applied,
        Action close,
        Func<Task> storedDataDeleted,
        Action openDownloads)
    {
        _store = store;
        _cache = cache;
        _paths = paths;
        _applied = applied;
        _close = close;
        _openDownloads = openDownloads;
        _storedDataDeleted = storedDataDeleted;
    }

    [ObservableProperty]
    public partial SettingsTab Tab { get; set; } = SettingsTab.Memory;

    public bool IsMemoryTab => Tab == SettingsTab.Memory;

    public bool IsDataTab => Tab == SettingsTab.YourData;

    public bool IsRuntimeTab => Tab == SettingsTab.Runtime;

    public bool IsConfigTab => Tab == SettingsTab.Config;

    /// <summary>
    /// The engine's configuration, editable. The reference engine's own design
    /// says adding a language is a JSON edit, so the files have to be reachable
    /// from inside the application rather than only by someone who knows where
    /// the assembly keeps them.
    /// </summary>
    public IReadOnlyList<ConfigFileViewModel> ConfigFiles { get; } = BuildConfigFiles();

    private static IReadOnlyList<ConfigFileViewModel> BuildConfigFiles()
    {
        var store = BetterTranslator.Engine.Config.ConfigStore.Active
            ?? new BetterTranslator.Engine.Config.ConfigStore(
                Path.Combine(AppPaths.DefaultRoot, "config"));

        return [.. BetterTranslator.Engine.Config.ConfigStore.Files.Select(f => new ConfigFileViewModel(f, store))];
    }

    [ObservableProperty]
    public partial ConfigFileViewModel? SelectedConfig { get; set; }

    [RelayCommand]
    private void ShowConfig()
    {
        Tab = SettingsTab.Config;
        SelectedConfig ??= ConfigFiles.FirstOrDefault();
    }

    [RelayCommand]
    private void ChooseConfig(ConfigFileViewModel file) => SelectedConfig = file;

    public bool IsDownloadsTab => Tab == SettingsTab.Downloads;

    public bool IsAgentTab => Tab == SettingsTab.Agent;

    public bool IsUpdatesTab => Tab == SettingsTab.Updates;

    public UpdatesViewModel Updates { get; init; } = new();

    [RelayCommand]
    private void ShowUpdates() => Tab = SettingsTab.Updates;

    [ObservableProperty]
    public partial bool McpEnabled { get; set; }

    [ObservableProperty]
    public partial string McpHost { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial string McpPort { get; set; } = "8765";

    [ObservableProperty]
    public partial string McpToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string McpStatus { get; set; } = "Off.";

    public string McpCommand => Mcp.McpServerHost.RegistrationCommand(McpHost, PortOrDefault, McpToken);

    public string McpStdioCommand => Mcp.McpServerHost.StdioCommand();

    public bool McpNeedsToken => !Mcp.McpServerHost.IsLoopback(McpHost) && McpToken.Length == 0;

    private int PortOrDefault => int.TryParse(McpPort, out var port) ? port : 8765;

    partial void OnMcpEnabledChanged(bool value) => PersistAgent();

    partial void OnMcpHostChanged(string value)
    {
        OnPropertyChanged(nameof(McpCommand));
        OnPropertyChanged(nameof(McpNeedsToken));
        PersistAgent();
    }

    partial void OnMcpPortChanged(string value)
    {
        OnPropertyChanged(nameof(McpCommand));
        PersistAgent();
    }

    partial void OnMcpTokenChanged(string value)
    {
        OnPropertyChanged(nameof(McpCommand));
        OnPropertyChanged(nameof(McpNeedsToken));
        PersistAgent();
    }

    [RelayCommand]
    private void ShowAgent() => Tab = SettingsTab.Agent;

    [RelayCommand]
    private void CopyMcpCommand() => Copy(McpCommand);

    [RelayCommand]
    private void CopyMcpStdioCommand() => Copy(McpStdioCommand);

    private void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            McpStatus = "Command copied. Paste it where the agent runs.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            McpStatus = "The clipboard was busy. Select the command and copy it by hand.";
        }
    }

    private void PersistAgent()
    {
        if (_loading)
        {
            return;
        }

        var refusal = Mcp.McpServerHost.Refuse(new AppSettings
        {
            McpEnabled = McpEnabled,
            McpHost = McpHost.Trim(),
            McpPort = PortOrDefault,
            McpToken = McpToken.Trim(),
        });

        McpStatus = refusal ?? (McpEnabled ? $"Listening on http://{McpHost.Trim()}:{PortOrDefault}/mcp." : "Off.");

        Write(settings =>
        {
            settings.McpEnabled = McpEnabled;
            settings.McpHost = McpHost.Trim();
            settings.McpPort = PortOrDefault;
            settings.McpToken = McpToken.Trim();
        });
    }

    /// <summary>Backend cards, one per flavour, in speed order.</summary>
    public ObservableCollection<BackendOptionViewModel> Backends { get; } = [];

    /// <summary>Every GGUF found in the models folder.</summary>
    public ObservableCollection<ModelOptionViewModel> Models { get; } = [];

    /// <summary>Everything this build supports, installed or not.</summary>
    public ObservableCollection<CatalogueRow> Catalogue { get; } = [];

    /// <summary>What the probe found, for the line above the cards.</summary>
    [ObservableProperty]
    public partial string HardwareLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RuntimeBackend SelectedBackend { get; set; } = RuntimeBackend.Cpu;

    [ObservableProperty]
    public partial ModelOptionViewModel? SelectedModel { get; set; }

    /// <summary>
    /// The flavour actually loaded in this process. Set by whoever starts the
    /// runtime; a mismatch with the selection is what raises the restart notice.
    /// </summary>
    [ObservableProperty]
    public partial RuntimeBackend RunningBackend { get; set; } = RuntimeBackend.Cpu;

    /// <summary>
    /// Windows will not swap a native library that is already loaded, so a
    /// backend change is honest about waiting for the next start rather than
    /// pretending to take effect now.
    /// </summary>
    public bool NeedsRestart => SelectedBackend != RunningBackend;

    public bool HasModels => Models.Count > 0;

    /// <summary>
    /// Names every folder searched, not just the app's own. Models are usually
    /// already on the machine from another tool, and a label naming one folder
    /// while listing models from another reads as a bug.
    /// </summary>
    public string ModelsFolderLabel =>
        "Searched: " + string.Join("  and  ", ModelLibrary.DefaultFolders(_paths.ModelsFolder)) +
        ". The chosen model loads when you send your first message.";

    // ---- Memory ----

    [ObservableProperty]
    public partial bool LearnFromMyEdits { get; set; } = true;

    [ObservableProperty]
    public partial bool UnderlineMemoryWords { get; set; } = true;

    [ObservableProperty]
    public partial bool ReindexFilesWhenTheyChange { get; set; } = true;

    [ObservableProperty]
    public partial bool ScopeIsWholeProject { get; set; } = true;

    /// <summary>Whole percent. Anything less certain shows dotted and under Unsure.</summary>
    [ObservableProperty]
    public partial double UnsureThreshold { get; set; } = 80;

    public string UnsureThresholdLabel => $"{(int)UnsureThreshold}%";

    public static string UnsureThresholdNote =>
        "Anything the engine is less certain about than this gets a dotted underline and shows up under Unsure.";

    // ---- Your data ----

    public ObservableCollection<CacheEntry> CacheRows { get; } = [];

    public ObservableCollection<InstalledModelRow> InstalledModels { get; } = [];

    [ObservableProperty]
    public partial string CacheTotalLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelsTotalLabel { get; set; } = string.Empty;

    /// <summary>
    /// The application data root, not the models subfolder. This screen is about
    /// the database, the caches and the index; pointing it at models/ opened a
    /// folder holding none of them.
    /// </summary>
    /// <summary>What the last folder change did, or why it did not happen.</summary>
    [ObservableProperty]
    public partial string DataFolderStatus { get; set; } = string.Empty;

    /// <summary>
    /// A copy finished and the pointer is written, so the new folder is in force
    /// on the next start. Shows the restart button beside the status rather than
    /// leaving the reader to find one.
    /// </summary>
    [ObservableProperty]
    public partial bool DataFolderMoved { get; set; }

    public string DataFolder =>
        Directory.GetParent(_paths.DefaultModelsFolder)?.FullName ?? _paths.DefaultModelsFolder;

    /// <summary>
    /// C26. True once Delete everything has been pressed: the warning replaces
    /// its own trigger in place rather than stacking beneath it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingDeleteEverything { get; set; }

    public bool ShowDeleteEverythingTrigger => !IsConfirmingDeleteEverything;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _loading = true;

        var settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _settings = settings;

        LearnFromMyEdits = settings.LearnFromMyEdits;
        UnderlineMemoryWords = settings.UnderlineMemoryWords;
        ReindexFilesWhenTheyChange = settings.ReindexFilesWhenTheyChange;
        ScopeIsWholeProject = settings.DefaultScopeForNewChats == ChatScope.WholeProject;
        UnsureThreshold = settings.UnsureThresholdPercent;
        SelectedBackend = settings.RuntimeBackend;

        McpEnabled = settings.McpEnabled;
        McpHost = settings.McpHost;
        McpPort = settings.McpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        McpToken = settings.McpToken;
        McpStatus = Mcp.McpServerHost.Refuse(settings)
            ?? (settings.McpEnabled ? $"Listening on http://{settings.McpHost}:{settings.McpPort}/mcp." : "Off.");

        LoadRuntimeTab(settings);

        _loading = false;

        await RefreshDataAsync(cancellationToken).ConfigureAwait(true);
        await Updates.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Re-reads sizes from disk. Called after every delete.</summary>
    public async Task RefreshDataAsync(CancellationToken cancellationToken)
    {
        var rows = await _cache.InspectAsync(cancellationToken).ConfigureAwait(true);

        CacheRows.Clear();
        foreach (var row in rows)
        {
            CacheRows.Add(row);
        }

        CacheTotalLabel = ByteSize.Format(CacheInspector.Total(rows));

        InstalledModels.Clear();
        long modelBytes = 0;

        // Runtimes as well as models. A runtime is a download like any other now
        // -- several flavours, tens of megabytes each -- so a data screen listing
        // only models would understate what is on disk and offer no way to
        // reclaim it.
        foreach (var component in AllComponents())
        {
            if (InstalledPath(component) is not { } path)
            {
                continue;
            }

            var bytes = Length(path);
            modelBytes += bytes;

            InstalledModels.Add(new InstalledModelRow(
                component.Id,
                component.Name,
                component.Kind == ComponentKind.Runtime ? "Runtime" : "Model - translation model",
                ByteSize.Format(bytes),
                CanDelete: !component.IsRequired));
        }

        ModelsTotalLabel = ByteSize.Format(modelBytes);
    }

    [RelayCommand]
    private void ShowMemory() => Tab = SettingsTab.Memory;

    [RelayCommand]
    private void ShowYourData() => Tab = SettingsTab.YourData;

    [RelayCommand]
    private void ShowRuntime() => Tab = SettingsTab.Runtime;

    [RelayCommand]
    private void ShowDownloads() => Tab = SettingsTab.Downloads;

    /// <summary>
    /// Fills the Runtime tab: what the machine can run, what has shipped, and
    /// which models are on disk.
    /// </summary>
    private void LoadRuntimeTab(AppSettings settings)
    {
        var hardware = BackendCatalog.Probe();
        HardwareLabel = hardware.VendorLabel;

        // The two halves of a flavour, joined here because this is the only
        // place that sees both: BackendCatalog knows what can run and what is on
        // disk, ComponentCatalog knows what it costs and where it comes from.
        var priced = ComponentCatalog.RuntimesFor(hardware);

        Backends.Clear();
        foreach (var option in _backends.Options(hardware))
        {
            var catalogue = priced.FirstOrDefault(
                c => c.Id == "betterruntime-" + option.Backend.ToString().ToLowerInvariant());

            Backends.Add(new BackendOptionViewModel(option, catalogue, b => b == SelectedBackend));
        }

        // A stored choice can outlive the hardware or the DLL that served it.
        if (Backends.FirstOrDefault(b => b.Backend == SelectedBackend) is { CanSelect: false })
        {
            SelectedBackend = _backends.Recommend(hardware);
        }

        Models.Clear();
        var found = _library.Scan([.. ModelLibrary.DefaultFolders(_paths.ModelsFolder)]);
        foreach (var model in found)
        {
            Models.Add(new ModelOptionViewModel(model, p => p == SelectedModel?.Model.Path));
        }

        SelectedModel =
            Models.FirstOrDefault(m => string.Equals(m.Model.Path, settings.SelectedModelPath, StringComparison.OrdinalIgnoreCase))
            ?? (_library.Default(found) is { } fallback
                ? Models.FirstOrDefault(m => m.Model.Path == fallback.Path)
                : null);

        Catalogue.Clear();
        foreach (var component in AllComponents())
        {
            Catalogue.Add(new CatalogueRow(
                component.Name,
                component.Kind == ComponentKind.Runtime ? "Runtime" : component.Summary,
                ByteSize.Format(component.InstallBytes),
                InstalledPath(component) is not null,
                component.IsFetchable,
                component.IsSupported));
        }

        RefreshChoices();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ModelsFolderLabel));
    }

    /// <summary>
    /// Sends a flavour that is runnable but absent to the one installer.
    ///
    /// Settings closes on the way. The installer is a modal layer of its own and
    /// leaving this one open behind it would stack two, and the row this screen
    /// shows would be stale the moment the download finished anyway.
    /// </summary>
    [RelayCommand]
    private void GetFlavour(BackendOptionViewModel option)
    {
        if (!option.CanFetch)
        {
            return;
        }

        _close();
        _openDownloads();
    }

    [RelayCommand]
    private void ChooseBackend(BackendOptionViewModel option)
    {
        if (!option.CanSelect)
        {
            return;
        }

        SelectedBackend = option.Backend;
    }

    /// <summary>
    /// The one path where a person chooses a model, which is the only thing that
    /// earns the right to write one. Everything else that assigns
    /// <see cref="SelectedModel"/> -- opening the screen, rescanning the folder,
    /// falling back when a stored path has gone -- is the screen catching up
    /// with what is on disk, not a choice, and must not overwrite a model
    /// another surface selected in the meantime.
    /// </summary>
    [RelayCommand]
    private void ChooseModel(ModelOptionViewModel option)
    {
        _modelChosenHere = true;
        SelectedModel = option;
    }

    [RelayCommand]
    private void RescanModels()
    {
        // The row as it stands now, not as it stood when the screen opened: the
        // snapshot is only replaced after a write completes, so rescanning
        // against it could snap the selection back over a newer choice.
        _settings.SelectedModelPath = SelectedModel?.Model.Path ?? _settings.SelectedModelPath;

        LoadRuntimeTab(_settings);
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private async Task DeleteCacheRowAsync(CacheEntry row)
    {
        await _cache.DeleteAsync(row.Id, CancellationToken.None).ConfigureAwait(true);

        // A single row can be the chat history, so the chat reloads either way.
        await _storedDataDeleted().ConfigureAwait(true);
        await RefreshDataAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// The outcome of the last removal, said out loud.
    ///
    /// This used to be silent. A press that could not delete anything -- and the
    /// runtime this session loaded can never be deleted -- swallowed its
    /// exception, refreshed the list, and left the row exactly where it was. It
    /// read as a button that does nothing, and the file was still there after a
    /// restart because it had never been removed at all.
    /// </summary>
    [ObservableProperty]
    public partial string? DataMessage { get; set; }

    public bool HasDataMessage => !string.IsNullOrEmpty(DataMessage);

    partial void OnDataMessageChanged(string? value) => OnPropertyChanged(nameof(HasDataMessage));

    /// <summary>
    /// Why this component cannot be removed, or null when it can.
    ///
    /// Both refusals are about runtimes, and both are real rather than
    /// protective: Windows will not delete a library the process has loaded, and
    /// an installation with no runtime at all cannot translate and offers no
    /// route to fix itself from inside the application.
    /// </summary>
    private string? RemoveBlockedReason(ModelComponent component)
    {
        if (component.Kind != ComponentKind.Runtime)
        {
            return null;
        }

        if (string.Equals(component.FileName, BackendCatalog.FileNameFor(RunningBackend), StringComparison.OrdinalIgnoreCase))
        {
            return $"{component.Name} is the runtime this session is using, and Windows will not delete a library "
                + "that is loaded. Pick another runtime above, restart, then remove this one.";
        }

        var installed = AllComponents()
            .Where(c => c.Kind == ComponentKind.Runtime && InstalledPath(c) is not null)
            .ToList();

        return installed.Count <= 1
            ? $"{component.Name} is the only runtime installed. Removing it would leave nothing able to translate, "
                + "so install another one first."
            : null;
    }

    [RelayCommand]
    private async Task DeleteModelAsync(InstalledModelRow row)
    {
        var component = AllComponents().FirstOrDefault(c => c.Id == row.Id);

        if (component is null || component.IsRequired)
        {
            return;
        }

        if (RemoveBlockedReason(component) is { } blocked)
        {
            DataMessage = blocked;
            return;
        }

        // Deleted from wherever it actually is. A runtime downloaded into the
        // models folder and one copied in by the build sit in different places,
        // and removing only the first would leave the row on screen.
        var path = InstalledPath(component);

        if (path is null)
        {
            DataMessage = $"{component.Name} is not on this machine.";
            return;
        }

        try
        {
            File.Delete(path);
            DataMessage = $"Removed {component.Name} from {Path.GetDirectoryName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named rather than swallowed. A row that stays put with no
            // explanation is indistinguishable from a broken button.
            DataMessage = $"{component.Name} could not be removed: {ex.Message}";
        }

        await RefreshDataAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Every component this build knows about, runtimes included.</summary>
    private static IEnumerable<ModelComponent> AllComponents() =>
        ComponentCatalog.RuntimesFor(BackendCatalog.Probe()).Concat(ComponentCatalog.BuiltIn);

    /// <summary>
    /// Where a component actually is, across every folder a flavour may sit in,
    /// or null when it is not installed.
    ///
    /// A folder only counts if everything the component needs is in it. The
    /// files have to be together, not merely all present somewhere, because that
    /// is how the loader resolves a DLL's imports -- from the folder of the DLL
    /// itself. Reporting a folder that holds the runtime but not its libraries
    /// would call a component installed that cannot load.
    /// </summary>
    private string? InstalledPath(ModelComponent component)
    {
        if (Complete(_paths.ModelsFolder, component))
        {
            return _paths.PathFor(component);
        }

        return component.Kind == ComponentKind.Runtime
            ? BackendCatalog.SearchPaths
                .Where(folder => Complete(folder, component))
                .Select(folder => Path.Combine(folder, component.FileName))
                .FirstOrDefault()
            : null;
    }

    private static bool Complete(string folder, ModelComponent component) =>
        ComponentInstallState.IsInstalledIn(folder, component);

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Restarts the application so a newly chosen runtime flavour takes effect.
    /// Windows will not swap a loaded native library, so this is the only way to
    /// honour the choice, and doing it here beats telling the reader to do it.
    /// </summary>
    [RelayCommand]
    private void Restart()
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        // Started before the shutdown so the new process exists even if this one
        // is killed rather than closed politely.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void BeginDeleteEverything() => IsConfirmingDeleteEverything = true;

    [RelayCommand]
    private void KeepEverything() => IsConfirmingDeleteEverything = false;

    [RelayCommand]
    private async Task DeleteEverythingAsync()
    {
        await _cache.DeleteEverythingAsync(CancellationToken.None).ConfigureAwait(true);

        IsConfirmingDeleteEverything = false;
        await RefreshDataAsync(CancellationToken.None).ConfigureAwait(true);

        // The chat behind this dialog is reading rows that are gone now.
        await _storedDataDeleted().ConfigureAwait(true);
    }

    /// <summary>
    /// Points the data folder somewhere else.
    ///
    /// Copies rather than moves, and writes the pointer only once every copy has
    /// succeeded. The database is open for the whole session, so moving it out
    /// from under the live connection is not available; copying is, and it also
    /// means a failure halfway leaves both folders intact rather than one broken
    /// one. The old folder is left in place deliberately, as the backup, and is
    /// named so it can be deleted by hand once the new location has proven
    /// itself.
    /// </summary>
    [RelayCommand]
    private async Task ChangeDataFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where BetterTranslator keeps its data",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var target = dialog.FolderName;
        var source = DataFolder;

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DataFolderMoved = false;

        // Before the file is read as a file. Write-ahead logging leaves the most
        // recent writes in a sidecar, and a .db copied without them is a
        // database missing everything since the last checkpoint.
        await _cache.SettleAsync(CancellationToken.None).ConfigureAwait(true);

        // Counted before anything is copied, so the count is a real denominator
        // rather than a number that climbs as the copy discovers more work.
        // The sidecars are left behind deliberately: they are derived state, the
        // checkpoint above has already folded them in, and copying a -wal beside
        // its database is how a copy ends up inconsistent.
        var files = await Task.Run(
            () => Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
                .ToArray())
            .ConfigureAwait(true);

        var total = ByteSize.Format(files.Sum(Length));
        DataFolderStatus = $"Copying 0 of {files.Length} files, {total}...";

        // IProgress captures the UI context here, so the copy thread never
        // touches a bound property directly.
        var progress = new Progress<int>(done =>
            DataFolderStatus = $"Copying {done} of {files.Length} files, {total}...");

        try
        {
            await Task.Run(() => CopyTree(source, target, files, progress)).ConfigureAwait(true);

            Directory.CreateDirectory(AppPaths.DefaultRoot);
            await File.WriteAllTextAsync(AppPaths.RedirectFile, target).ConfigureAwait(true);

            DataFolderStatus = $"Copied {files.Length} files. The old folder is kept as a backup.";
            DataFolderMoved = true;

            // The reset button appears the moment a pointer exists, without
            // needing Settings reopened to notice.
            OnPropertyChanged(nameof(HasCustomDataFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No pointer was written, so the app still uses the folder it always
            // did and nothing has been lost.
            DataFolderStatus = $"Copy failed, nothing changed: {ex.Message}";
        }
    }

    /// <summary>
    /// Recursive copy, overwriting, so a retry after a failure resumes rather
    /// than starting over.
    /// </summary>
    private static void CopyTree(string source, string target, string[] files, IProgress<int> progress)
    {
        Directory.CreateDirectory(target);

        for (var i = 0; i < files.Length; i++)
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, files[i]));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(files[i], destination, overwrite: true);

            // Every tenth file, and the last. One report per file on a cache of
            // thousands would queue more dispatcher work than copying.
            if (i % 10 == 0 || i == files.Length - 1)
            {
                progress.Report(i + 1);
            }
        }
    }

    /// <summary>True while a pointer file is sending the data somewhere else.</summary>
    public bool HasCustomDataFolder => File.Exists(AppPaths.RedirectFile);

    /// <summary>
    /// Back to the default location.
    ///
    /// Only the pointer is deleted; nothing is copied back. The change that set
    /// it copied rather than moved, so the default folder still holds everything
    /// it did before -- removing the pointer is the whole undo. Data written
    /// since the change stays in the custom folder, which is why the status says
    /// where to find it rather than implying it came along.
    /// </summary>
    [RelayCommand]
    private void ResetDataFolder()
    {
        var current = DataFolder;

        try
        {
            if (File.Exists(AppPaths.RedirectFile))
            {
                File.Delete(AppPaths.RedirectFile);
            }

            DataFolderStatus = $"Back to the default folder. Anything added since is still in {current}.";
            DataFolderMoved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DataFolderStatus = $"Could not reset, nothing changed: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasCustomDataFolder));
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        var folder = DataFolder;

        // Created first: Explorer opens nothing at all for a path that is not
        // there, which is exactly what "the button does nothing" looked like.
        Directory.CreateDirectory(folder);

        // explorer.exe with the folder as an argument, rather than shell
        // executing the path itself. The latter leans on a shell association for
        // directories and fails silently when that is not what it finds.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"")
        {
            UseShellExecute = true,
        });
    }

    partial void OnTabChanged(SettingsTab value)
    {
        OnPropertyChanged(nameof(IsMemoryTab));
        OnPropertyChanged(nameof(IsDataTab));
        OnPropertyChanged(nameof(IsRuntimeTab));
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsDownloadsTab));
        OnPropertyChanged(nameof(IsAgentTab));
        OnPropertyChanged(nameof(IsUpdatesTab));
    }

    partial void OnSelectedBackendChanged(RuntimeBackend value)
    {
        RefreshChoices();
        OnPropertyChanged(nameof(NeedsRestart));
        Persist();
    }

    partial void OnRunningBackendChanged(RuntimeBackend value) => OnPropertyChanged(nameof(NeedsRestart));

    partial void OnSelectedModelChanged(ModelOptionViewModel? value)
    {
        RefreshChoices();
        Persist();
    }

    /// <summary>
    /// The cards render their own chosen state, so both lists are told when the
    /// selection moves rather than being rebuilt.
    /// </summary>
    private void RefreshChoices()
    {
        foreach (var backend in Backends)
        {
            backend.Refresh();
        }

        foreach (var model in Models)
        {
            model.Refresh();
        }
    }

    partial void OnIsConfirmingDeleteEverythingChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDeleteEverythingTrigger));

    partial void OnUnsureThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(UnsureThresholdLabel));
        Persist();
    }

    partial void OnLearnFromMyEditsChanged(bool value) => Persist();

    partial void OnUnderlineMemoryWordsChanged(bool value) => Persist();

    partial void OnReindexFilesWhenTheyChangeChanged(bool value) => Persist();

    partial void OnScopeIsWholeProjectChanged(bool value) => Persist();

    /// <summary>
    /// Saves on every change and hands the new settings back, so a toggle takes
    /// effect immediately rather than on some later apply.
    /// </summary>
    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        Write(settings =>
        {
            settings.LearnFromMyEdits = LearnFromMyEdits;
            settings.UnderlineMemoryWords = UnderlineMemoryWords;
            settings.ReindexFilesWhenTheyChange = ReindexFilesWhenTheyChange;
            settings.DefaultScopeForNewChats = ScopeIsWholeProject ? ChatScope.WholeProject : ChatScope.ThisChat;
            settings.UnsureThresholdPercent = (int)UnsureThreshold;
            settings.RuntimeBackend = SelectedBackend;

            // Only when this screen is the one that moved it. Every toggle up
            // there writes the whole row, so re-asserting a selection nobody
            // touched is how an agent's select_model was undone by someone
            // ticking an unrelated box -- and how "no card selected" was
            // written out as no model at all.
            if (_modelChosenHere && SelectedModel is not null)
            {
                settings.SelectedModelPath = SelectedModel.Model.Path;

                // Spent. The choice is now in the row like anyone else's, and
                // re-asserting it on some later unrelated toggle would undo
                // whatever has chosen a model since.
                _modelChosenHere = false;
            }
        });
    }

    /// <summary>
    /// Read, change, write -- never write what was read when the screen opened.
    ///
    /// The store keeps one row of all the keys, so a save here carries every
    /// setting, including the ones this screen never shows. Saving the snapshot
    /// it loaded meant anything changed since -- by an agent calling
    /// select_model, by the composer's target, by the effort a model switch
    /// implies -- was silently rolled back by the next toggle. Re-reading first
    /// makes this screen the author of its own fields and no one else's.
    ///
    /// Writes are chained rather than fired in parallel: two toggles in quick
    /// succession would otherwise both read, both change, and the slower one
    /// would win with the older row underneath it.
    /// </summary>
    private void Write(Action<AppSettings> change)
    {
        _writing = WriteAsync(_writing, change);
    }

    private async Task WriteAsync(Task previous, Action<AppSettings> change)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Swallowed so one failed write does not stop the next from being
            // attempted. Nothing surfaces it: a settings row that will not save
            // means the database is gone, which the screen has no way to say
            // and no way to fix.
        }

        var settings = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        change(settings);

        _settings = settings;

        await _store.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);

        // Outside the chain. Applying settings restarts the agent server, which
        // builds a gateway and a web host; holding the next row write behind
        // that would make every toggle wait on the one before it.
        _ = _applied(settings);
    }
}
