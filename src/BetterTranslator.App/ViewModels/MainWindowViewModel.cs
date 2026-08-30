using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterTranslator.App.Controls;
using BetterTranslator.App.Services;
using System.Net.Http;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using BetterTranslator.Indexing.Retrieval;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using BetterTranslator.Runtime.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BetterTranslator.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherTimer _sizeReadoutTimer;
    private readonly DispatcherTimer _restartCountdown;
    private readonly Core.Services.Restart.RestartGuard _restartGuard = new();
    private readonly RestartLog _restartLog;
    private readonly Database _database;
    private readonly InstallPaths _installPaths;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly LocalTranslator _translator = new();
    private readonly SettingsStore _settingsStore;

    /// <summary>
    /// The stored settings as loaded, mutated in place and written back. Held
    /// rather than reloaded so that saving one row -- the effort, from the
    /// composer -- cannot write defaults over the rest of them.
    /// </summary>
    private AppSettings _settings = new();

    private Task _storing = Task.CompletedTask;

    private string _selectedModelPath = string.Empty;

    /// <summary>
    /// The one installer. Survives being dismissed while it is still
    /// transferring; see <see cref="Installer"/>.
    /// </summary>
    private FirstRunViewModel? _installer;

    public MainWindowViewModel()
    {
        // Phase 6 moves this to the host's service container; until then the
        // shell owns the store directly.
        var paths = new AppPaths();

        // Before anything reads a registry. The language list and the prompt
        // shapes are cached on first use, so pointing the engine at the folder
        // afterwards would load the shipped copies and ignore the reader's.
        Engine.Config.ConfigStore.UseFolder(Path.Combine(paths.Root, "config"));

        _database = new Database(paths);
        _installPaths = new InstallPaths(paths);
        _settingsStore = new SettingsStore(_database);
        _restartLog = new RestartLog(Path.Combine(paths.Root, "logs"));

        var indexStore = new IndexStore(_database);
        Memory = new MemoryViewModel(indexStore, new IndexingService(indexStore, new DocumentReaders()));

        // Both blocked routes land here: the effort menu item and the radio's
        // Get it button.
        Translation = new TranslationSettingsViewModel(_installPaths, OpenDownloadManager);

        // The runtime made visible: what is loaded, on which backend, and a way
        // to start or unload it without sending anything.
        Runtime = new RuntimeStatusViewModel(_translator, () => _selectedModelPath, () => RunningBackend);

        Clock = new ClockService();
        Workspace = new ChatWorkspaceViewModel(
            new ChatStore(_database),
            Clock,
            dialog =>
            {
                ActiveDialog = dialog;
                return dialog;
            });

        // The chat asks memory what it knows; the map logs what it did.
        Workspace.LookupMemory = (phrase, threshold) =>
            Memory.Lookup.Lookup(phrase, queryVector: null, threshold);
        Workspace.MemoryLookupCompleted = lookup => Memory.Map.LogLookup(lookup);

        // Only reached when the composer's Memory chip is attached. Everything
        // it returns -- this chat's earlier pairs, and the passages retrieval
        // found in the indexed project or folder -- is the whole of what the
        // model gets besides the text.
        Workspace.BuildMemoryContext = (phrase, pairs) =>
        {
            var lookup = Memory.Lookup.Lookup(phrase, queryVector: null, UnsureThresholdPercent);
            return MemoryContext.Build(pairs, Memory.Lookup.Passages(lookup.Hits));
        };

        // Sending is what starts the runtime. Loading a model costs seconds and
        // gigabytes, and most sessions open the window before they translate.
        Workspace.Translate = async (ask, cancellationToken) =>
        {
            // Everything the composer sends is a unit somebody typed. The chat
            // has already cut the message up -- by line and sentence for prose,
            // by block for Markdown, by value for JSON -- so what arrives here
            // is never a document chunk, and the two gates calibrated for
            // document chunks are relaxed accordingly.
            var job = TranslationJobs.For(
                new TranslationRequest(ask.Text, ask.Direction, _selectedModelPath)
                {
                    Memory = ask.Memory,
                    UsesMemory = ask.UsesMemory,
                    IsStandalone = true,
                },
                _settings);

            // Straight to the model. The blast-radius argument that used to send
            // multi-line text through the document path is now answered earlier
            // and better: the chat segments before it calls, so one bad line
            // cannot refuse ten, and each of the three paths knows how its own
            // text is meant to be cut. Re-segmenting here would cut it twice --
            // and would have handed a batched JSON request to a pipeline that
            // translates line by line.
            var outcome = await _translator.TranslateAsync(job, cancellationToken).ConfigureAwait(true);

            ReportSamplerAdvisories(job);

            // The panel is the only place the cost of a run survives after the
            // entry has been written.
            Runtime.LastRun = outcome.GeneratedTokens > 0
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"{outcome.GeneratedTokens} tokens in {outcome.Duration.TotalSeconds:0.0} s ({outcome.TokensPerSecond ?? 0:0.#} tok/s)")
                : null;

            Runtime.RaiseState();
            return outcome;
        };

        // Why the last send produced no translation, for the entry to show. A
        // refusal that keeps the source is indistinguishable from a send that
        // never happened unless the reason is carried out with it.
        Workspace.LastVerdict = () => _translator.LastGuardVerdict;

        // The verifier is asked once, by the chat, after the units have been
        // spliced back together. Null when no verifier was built, which is the
        // state on any machine without a dictionary configured.
        Workspace.Verify = (sourceText, resultText) => _verifier?.Verify(sourceText, resultText);

        // D6: the model that just translated the first message names the chat.
        // The same resident model, so nothing is loaded and nothing is downloaded
        // for a chat name.
        Workspace.NameChat = (ask, cancellationToken) =>
            new ChatNamer(_translator).NameAsync(
                new ChatNameRequest
                {
                    Source = ask.Source,
                    ModelPath = _selectedModelPath,
                    Direction = ask.Direction,
                    Provisional = ask.Provisional,
                },
                cancellationToken);

        // Translating a whole file is the same path with the document floor kept:
        // a file legitimately holds short markup lines that are not prose, where
        // every line of a composer send was typed to be translated.
        Workspace.Preview.TranslateDocument = async (text, progress, cancellationToken) =>
        {
            // A file is translated against the project it belongs to, so the
            // glossary applies here whenever the reader has one. The composer's
            // chip is a per-message choice and there is no composer in this path.
            var result = await _translator.TranslateDocumentAsync(
                TranslationJobs.For(
                    new TranslationRequest(text, Workspace.Direction, _selectedModelPath)
                    {
                        UsesMemory = true,
                        IsStandalone = false,
                    },
                    _settings),
                progress,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            Runtime.LastRun = result.GeneratedTokens > 0
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"{result.GeneratedTokens} tokens in {result.Duration.TotalSeconds:0.0} s over {result.LinesTranslated} line(s)")
                : null;

            Runtime.RaiseState();
            return result;
        };

        // Switching effort switches the model, so the path is re-resolved here

        // rather than at the next send: picking Thinking and getting Fast's
        // model would be indistinguishable from the setting doing nothing.
        Translation.EffortChanged += effort =>
        {
            _settings.Effort = effort;
            _selectedModelPath = ResolveModelPath();
            ApplyModelLanguages();
            Store(settings => settings.Effort = effort);
        };

        // Advanced is stored now, so what an agent sends is what the reader set.
        Translation.TemperatureChanged += temperature =>
        {
            _settings.Temperature = temperature;
            Store(settings => settings.Temperature = temperature);
        };

        Translation.InstructionChanged += instruction =>
        {
            _settings.Instruction = instruction;
            Store(settings => settings.Instruction = instruction);
        };

        ApplyModelLanguages();

        WorkspaceModes = new ObservableCollection<SegmentItem>(BuildWorkspaceModes());

        SelectedWorkspaceMode = WorkspaceModes[1];

        _sizeReadoutTimer = new DispatcherTimer { Interval = Tokens.Milliseconds("SizePillHideDelayMs") };
        _sizeReadoutTimer.Tick += (_, _) =>
        {
            _sizeReadoutTimer.Stop();
            IsSizeReadoutVisible = false;
        };

        _restartCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _restartCountdown.Tick += (_, _) => RestartNotice?.Tick();
    }

    public ObservableCollection<SegmentItem> WorkspaceModes { get; }

    /// <summary>
    /// The three workspace segments. Built apart from the constructor, which
    /// opens the database on its first line, so what the segmented control is
    /// handed can be read in a test without a store on disk.
    /// </summary>
    internal static SegmentItem[] BuildWorkspaceModes() =>
    [
        new SegmentItem
        {
            Value = WorkspaceMode.Memory,
            Label = "Memory",
            Width = Tokens.Number("SegmentWidthMemory"),
            Icon = Tokens.Get<IconDefinition>("IconMemoryGraphSmall"),
            ActiveForeground = Tokens.Get<Brush>("BrushAccentDeep"),

            // Locked rather than hidden: the mode is part of what the app is
            // for, and a segment that vanishes reads as a bug where one that
            // says why reads as a decision. Nothing else selects
            // WorkspaceMode.Memory, so this segment is the whole gate.
            IsLocked = true,
            LockedReason = "Memory is coming soon",
        },
        new SegmentItem
        {
            Value = WorkspaceMode.Simple,
            Label = "Simple",
            Width = Tokens.Number("SegmentWidthSimple"),
        },
        new SegmentItem
        {
            Value = WorkspaceMode.Advanced,
            Label = "Advanced",
            Width = Tokens.Number("SegmentWidthAdvanced"),
        },
    ];

    public ClockService Clock { get; }

    public ChatWorkspaceViewModel Workspace { get; }

    /// <summary>The runtime panel behind the play/pause button in the caption.</summary>
    public RuntimeStatusViewModel Runtime { get; }

    public MemoryViewModel Memory { get; }

    /// <summary>S8. The effort menu in Simple, the radios and fields in Advanced.</summary>
    public TranslationSettingsViewModel Translation { get; }

    /// <summary>S13. Non-null while the Settings window is up.</summary>
    [ObservableProperty]
    public partial SettingsViewModel? Settings { get; set; }

    public UpdatesViewModel Updates { get; } = new();

    /// <summary>
    /// The flavour this process actually loaded. Fixed at startup, and what the
    /// settings screen compares against to decide whether to ask for a restart.
    /// </summary>
    public RuntimeBackend RunningBackend { get; private set; } = RuntimeBackend.Cpu;

    public bool HasSettings => Settings is not null;

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = new SettingsViewModel(
            new SettingsStore(_database),
            new CacheInspector(new AppPaths(), _database),
            _installPaths,
            ApplySettingsAsync,
            () => Settings = null,
            ReloadAfterDeletionAsync,
            OpenDownloads)
        {
            Updates = Updates,
        };

        settings.RunningBackend = RunningBackend;

        Settings = settings;
        await settings.LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// A toggle takes effect immediately: the threshold changes which words
    /// render dotted without anything being translated again.
    /// </summary>
    /// <summary>
    /// Read, change, write, one at a time. The shell holds a snapshot of the
    /// settings row and the row has other authors now -- the Settings screen, and
    /// an agent calling select_model -- so saving the snapshot would roll their
    /// changes back. Only the field named here is written.
    /// </summary>
    private void Store(Action<AppSettings> change) => _storing = StoreAsync(_storing, change);

    private async Task StoreAsync(Task previous, Action<AppSettings> change)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        var settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        change(settings);

        await _settingsStore.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        UnsureThresholdPercent = settings.UnsureThresholdPercent;
        UnderlineMemoryWords = settings.UnderlineMemoryWords;

        // Re-marks what is already on screen, without translating again.
        Workspace.UnsureThreshold = settings.UnsureThresholdPercent;

        // A different model can be picked up on the next send; a different
        // backend cannot, because its DLL is already loaded.
        _settings = settings;
        _selectedModelPath = ResolveModelPath();

        // Removing a model from Settings has to block the effort it backed,
        // rather than leaving a menu entry that loads nothing.
        Translation.RefreshInstalled();

        await ApplyAgentServerAsync(settings).ConfigureAwait(true);
    }

    public Mcp.McpServerHost AgentServer { get; } =
        new(new Mcp.AppGuiBridge(() => Application.Current?.MainWindow?.DataContext as MainWindowViewModel));

    public async Task ApplyAgentServerAsync(AppSettings settings)
    {
        if (!settings.McpEnabled)
        {
            await AgentServer.StopAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (AgentServer.IsListening)
        {
            await AgentServer.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await AgentServer.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
    }

    public void ReloadSettingsFromDisk() => _ = ReloadSettingsAsync();

    private async Task ReloadSettingsAsync()
    {
        _settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        _selectedModelPath = ResolveModelPath();
        Translation.RefreshInstalled();
    }

    /// <summary>
    /// Reads the stored runtime choice before anything can touch the native
    /// library. The flavour is fixed for the life of the process, so this has
    /// to run first and the settings screen says as much.
    /// </summary>
    private async Task StartRuntimeAsync(CancellationToken cancellationToken)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);

        // The target language decides which dictionary is the right one, so it is
        // resolved to a code here rather than left to the engine to guess.
        var paths = new AppPaths();

        _verifier = VerificationFactory.Create(
            _settings.Verification,
            new Engine.Languages.LanguageRegistry().Resolve(_settings.TargetLanguage)?.Code,
            paths.DictionariesFolder);

        // A downloaded flavour lands in the models folder, so the loader has to
        // look there as well as beside the executable. Registered before the
        // first native call, because the flavour is resolved once and kept.
        BackendCatalog.SearchAlso(_installPaths.ModelsFolder);

        RunningBackend = new BackendCatalog().Resolve(_settings.RuntimeBackend);
        LocalTranslator.Prefer(RunningBackend);

        // Before the path is resolved: the effort is what decides which model
        // that resolution is looking for.
        Translation.Restore(_settings.Effort);
        Translation.RestoreAdvanced(_settings.Temperature, _settings.Instruction);

        _selectedModelPath = ResolveModelPath();

        await ApplyAgentServerAsync(_settings).ConfigureAwait(true);
    }

    /// <summary>
    /// Repartitions the composer's language list into what the chosen model was
    /// actually trained on and what it was not.
    ///
    /// This is the one failure nothing else in the stack can catch. Every gate
    /// compares structure, placeholders and terminology, never meaning -- so a
    /// model asked for a language it has never seen returns something
    /// structurally perfect and wrong, and the run reports success. Refusing the
    /// language is the only place that can be prevented.
    /// </summary>
    private void ApplyModelLanguages()
    {
        Workspace.ModelId = Translation.Model.Id;
        Workspace.SetModelLanguages(
            TargetLanguage.ForCatalog(Translation.Model.Id),
            Translation.Model.Name);
    }

    /// <summary>
    /// The word-level verifier, or null when one could not be built. Built once
    /// at startup from the stored settings, so a machine with no dictionary
    /// configured simply never marks a word.
    ///
    /// Reached through <see cref="ChatWorkspaceViewModel.Verify"/> rather than
    /// applied here: the composer sends one unit at a time, and the pair worth
    /// verifying is the assembled answer against the whole message.
    /// </summary>
    private TranslationVerifier? _verifier;

    private void ReportSamplerAdvisories(TranslationJob job)
    {
        if (!SamplerConfigGuard.AppliesTo(job.ModelPath))
        {
            Runtime.ConfigAdvisories = null;
            return;
        }

        var applied = job.Sampling();

        // The override, said out loud. Validate is asked about what went out,
        // which by construction conforms: the guard wrote it. So the sampler
        // rules in there can never fire from here, and the difference between
        // what the reader set and what was sent has to be reported separately or
        // not at all. It was not at all.
        var notices = new List<string>();

        var overridden = SamplerAdvisory.Describe(Translation.Model.Name, job.Temperature, applied.Temperature);

        if (overridden is not null)
        {
            notices.Add(overridden);
        }

        notices.AddRange(SamplerConfigGuard
            .Validate(
                job.ModelPath,
                new SamplerSettings(applied.Temperature, applied.TopK, applied.TopP, 0, applied.RepeatPenalty),
                kvCacheQuantized: false)
            .Select(advisory => advisory.Message));

        Runtime.ConfigAdvisories = notices.Count == 0 ? null : string.Join(" ", notices);
    }

    private async Task<TranslationOutcome> TranslateAsDocumentAsync(
        TranslationJob job,
        CancellationToken cancellationToken)
    {
        var result = await _translator
            .TranslateDocumentAsync(job, progress: null, minLetters: 1, cancellationToken)
            .ConfigureAwait(true);

        // Nothing came back at all, so the entry says so rather than showing the
        // source as though it were the answer.
        return result.LinesTranslated == 0
            ? TranslationOutcome.None(result.Duration)
            : new TranslationOutcome(result.Text, result.GeneratedTokens, result.Duration);
    }

    /// <summary>
    /// The GGUF the next send will load. In order: the model the chosen effort
    /// names, then an explicit choice from Settings, then whatever is on disk --
    /// so a machine with models but no choices made still translates.
    ///
    /// The effort's model is looked up by file name across the folders actually
    /// scanned, never by composing a path out of its catalogue id. A model can
    /// sit under a publisher folder that has nothing to do with its id, and a
    /// constructed path reports a model that is present as missing.
    /// </summary>
    private string ResolveModelPath()
    {
        var library = new ModelLibrary();
        var found = library.Scan([.. ModelLibrary.DefaultFolders(_installPaths.ModelsFolder)]);

        var wanted = library.Match(found, Translation.Model.FileName);

        if (wanted is not null)
        {
            return wanted.Path;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedModelPath) && File.Exists(_settings.SelectedModelPath))
        {
            return _settings.SelectedModelPath;
        }

        return library.Default(found)?.Path ?? string.Empty;
    }

    [ObservableProperty]
    public partial int UnsureThresholdPercent { get; set; } = 80;

    [ObservableProperty]
    public partial bool UnderlineMemoryWords { get; set; } = true;

    partial void OnSettingsChanged(SettingsViewModel? value) => OnPropertyChanged(nameof(HasSettings));

    /// <summary>S11. Non-null while the Add to this project dialog is up.</summary>
    [ObservableProperty]
    public partial AddSourcesViewModel? AddSources { get; set; }

    public bool HasAddSources => AddSources is not null;

    [RelayCommand]
    private void OpenAddSources() =>
        AddSources = new AddSourcesViewModel(
            !Memory.IsEmpty,
            Memory.ProjectName,
            Workspace.Rows.Count,
            Workspace.TranslatedWordCount,
            request => Memory.IndexAsync(request),
            () => AddSources = null);

    partial void OnAddSourcesChanged(AddSourcesViewModel? value) => OnPropertyChanged(nameof(HasAddSources));

    /// <summary>
    /// C13: the modal layer. Non-null while a dialog is up; it is a layer
    /// inside MainWindow, never a second Window.
    /// </summary>
    [ObservableProperty]
    public partial ConfirmDialogViewModel? ActiveDialog { get; set; }

    public bool HasDialog => ActiveDialog is not null;

    /// <summary>Sidebar width, written back by its resize separator.</summary>
    [ObservableProperty]
    public partial double SidebarWidth { get; set; } = Tokens.Number("SidebarDefaultWidth");

    public double SidebarMinWidth { get; } = Tokens.Number("SidebarMinWidth");

    public double SidebarMaxWidth { get; } = Tokens.Number("SidebarMaxWidth");

    /// <summary>Right panel width, written back by its own resize separator.</summary>
    [ObservableProperty]
    public partial double RightPanelWidth { get; set; } = Tokens.Number("RightPanelDefaultWidth");

    public double RightPanelMinWidth { get; } = Tokens.Number("RightPanelMinWidth");

    public double RightPanelMaxWidth { get; } = Tokens.Number("RightPanelMaxWidth");

    /// <summary>File preview width, written back by its resize separator.</summary>
    [ObservableProperty]
    public partial double PreviewWidth { get; set; } = Tokens.Number("PreviewDefaultWidth");

    public double PreviewMinWidth { get; } = Tokens.Number("PreviewMinWidth");

    public double PreviewMaxWidth { get; } = Tokens.Number("PreviewMaxWidth");

    /// <summary>
    /// Opens the database and loads the chat list. Runs off the UI thread.
    /// </summary>
    /// <summary>
    /// S14. Non-null while the first-run modal is up, which is on a fresh
    /// install and until the runtime and a model are present.
    /// </summary>
    [ObservableProperty]
    public partial FirstRunViewModel? FirstRun { get; set; }

    public bool HasFirstRun => FirstRun is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Written every launch, not only when a restart is asked for. The
        // shipped executable hosts the runtime from an unpacked payload, so
        // which of the two paths a relaunch would take is the first thing
        // anybody reading this log needs to know.
        var relaunch = Core.Services.Restart.ProcessRelaunch.Resolve();

        _restartLog.Write(
            $"Launched. Process path {Environment.ProcessPath}, payload directory {AppContext.BaseDirectory}, "
            + $"managed entry {Environment.GetCommandLineArgs().FirstOrDefault()}. Resolved {relaunch.Detail}. "
            + $"Relaunch command line would be {relaunch.CommandLine}. "
            + $"Relaunched by an earlier install: {Environment.GetEnvironmentVariable(RestartLauncher.RelaunchedMarker) == "1"}.");

        await _database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        await StartRuntimeAsync(cancellationToken).ConfigureAwait(true);
        await Workspace.LoadAsync(cancellationToken).ConfigureAwait(true);
        await Memory.RefreshAsync(cancellationToken).ConfigureAwait(true);

        // Nothing installed means nothing can translate, so the first-run modal
        // opens over the chat rather than letting a route fail later.
        //
        // It only opens when installing is actually possible. While the
        // catalogue carries no download URLs (D7) the modal could never be
        // completed, and a required step that cannot be finished would leave
        // the application unusable. The reviewer checklist requires the
        // opposite: the app runs with no model installed, and every route
        // needing one reaches the download manager instead.
        if (!IsAnyModelInstalled() && CanInstallAnything)
        {
            FirstRun = Installer();
        }

        await Updates.LoadAsync(cancellationToken).ConfigureAwait(true);

        _ = Updates.WatchAsync(cancellationToken);
    }

    /// <summary>
    /// The installer, kept beyond a dismissal.
    ///
    /// A transfer outlives the panel it was started from: clicking away used to
    /// drop this view model while its download carried on, and the footer button
    /// then built a fresh one with its own DownloadManager, which raced the
    /// first for the same .part file and failed with an IO error. One instance
    /// means reopening rejoins the run in progress and shows its real state.
    /// </summary>
    private FirstRunViewModel Installer()
    {
        if (_installer is not null)
        {
            return _installer;
        }

        var installer = new FirstRunViewModel(_installPaths, _httpClient);
        installer.Finished += OnFirstRunFinished;
        installer.Dismissed += OnInstallerDismissed;
        installer.BatchInstalled += OnBatchInstalled;

        _installer = installer;
        return installer;
    }

    /// <summary>
    /// Releases the single instance mutex. Set by the application, which owns
    /// it: the shell cannot reach into App to let go of a handle, and a
    /// relaunch that leaves it held gives the new process a second instance it
    /// will refuse to be.
    /// </summary>
    public Func<CancellationToken, Task>? ReleaseSingleInstance { get; set; }

    /// <summary>
    /// How the process ends once the new one is up. Injected so a test can
    /// watch a restart run to completion without taking the test host down
    /// with it.
    /// </summary>
    public Action<int> ExitProcess { get; set; } =
        code => Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown(code));

    /// <summary>Non-null while the restart notice is up.</summary>
    [ObservableProperty]
    public partial RestartNoticeViewModel? RestartNotice { get; set; }

    public bool HasRestartNotice => RestartNotice is not null;

    partial void OnRestartNoticeChanged(RestartNoticeViewModel? value)
    {
        OnPropertyChanged(nameof(HasRestartNotice));

        if (value is null)
        {
            _restartCountdown.Stop();
        }
        else
        {
            _restartCountdown.Start();
        }
    }

    /// <summary>
    /// The order matters and it is the whole of constraint three. The runtime
    /// child goes first because it is ours to take down. The listener goes
    /// next, so the port is free before anything else races for it. The mutex
    /// goes last: while it is held nothing else can claim to be the running
    /// instance, and letting go of it early would let a second copy start into
    /// a half released process.
    /// </summary>
    internal Core.Services.Restart.RestartService BuildRestartService() => new(
        [
            new NamedRestartResource(
                "the model runtime and its child process",
                token => _translator.UnloadAsync(token)),
            new NamedRestartResource(
                "the MCP loopback listener and its port",
                token => AgentServer.StopAsync(token)),
            new NamedRestartResource(
                "the log file handles",
                _ =>
                {
                    _restartLog.Write("Log handles flushed before the relaunch.");
                    return Task.CompletedTask;
                }),
            new NamedRestartResource(
                "the single instance mutex",
                token => ReleaseSingleInstance?.Invoke(token) ?? Task.CompletedTask),
        ],
        new ShellActiveWork(_translator, AgentServer),
        _restartGuard,
        Core.Services.Restart.ProcessRelaunch.Resolve,
        RestartLauncher.Start,
        code => ExitProcess(code),
        _restartLog.Write,
        () => _settings.RestartAfterInstall);

    private void OnBatchInstalled(InstallBatch batch)
    {
        var service = BuildRestartService();

        var request = new Core.Services.Restart.RestartRequest
        {
            BatchId = batch.Id,
            Reason = batch.Summary,
        };

        var looked = service.Inspect(request);

        _restartLog.Write($"Install batch {batch.Id} finished: {batch.Summary} Restart check: {looked.Detail}");

        if (looked.Outcome is Core.Services.Restart.RestartOutcome.RefusedBySetting
            or Core.Services.Restart.RestartOutcome.RefusedByGuard)
        {
            return;
        }

        RestartNotice = new RestartNoticeViewModel(
            request,
            batch.Summary,
            looked.ActiveWork,
            cancelWork => service.RestartAsync(
                request with { CancelActiveWork = cancelWork },
                CancellationToken.None),
            () => RestartNotice = null,
            RememberRestartSetting);
    }

    private void RememberRestartSetting(bool on)
    {
        _settings.RestartAfterInstall = on;

        if (Settings is { } open)
        {
            open.RestartAfterInstall = on;
        }

        _storing = _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    /// <summary>
    /// Put away, not cancelled. The instance is only released once nothing is
    /// being transferred; while a download is live it is kept so the footer
    /// button reopens the same one.
    /// </summary>
    private void OnInstallerDismissed()
    {
        FirstRun = null;

        if (_installer is { IsInstalling: false })
        {
            _installer = null;
        }
    }

    /// <summary>
    /// Opens the same installer the first run shows, from the caption row.
    /// Downloading is not a one-time event: a second model, or a GPU runtime
    /// built later, needs a way in that does not involve clearing the app data
    /// folder to trigger the first-run check again.
    /// </summary>
    [RelayCommand]
    private void OpenDownloads()
    {
        if (FirstRun is not null)
        {
            return;
        }

        var installer = Installer();

        // Re-ask what is on disk, so a model installed since this was last open
        // shows as present. Skipped mid-transfer: the rows are being written by
        // the run in progress and re-resolving would fight it.
        if (!installer.IsInstalling)
        {
            installer.RefreshPresence();
        }

        FirstRun = installer;
    }

    private bool IsAnyModelInstalled() =>
        ComponentCatalog.BuiltIn.Any(c => c.Kind == ComponentKind.Model && _installPaths.IsInstalled(c));

    /// <summary>
    /// True once at least one component has somewhere to be fetched from.
    /// </summary>
    private static bool CanInstallAnything =>
        ComponentCatalog.BuiltIn.Any(c => c.DownloadUrl is not null);

    private void OnFirstRunFinished(StartChoice choice)
    {
        FirstRun = null;
        _installer = null;

        // Something may have just been installed. Without this the effort that
        // model backs stays blocked until the next launch, and the next send
        // still loads whatever was resolved at startup.
        Translation.RefreshInstalled();
        _selectedModelPath = ResolveModelPath();

        // Every card lands on the same empty chat, and the choice is not read.
        // Just chat by design; Repository was already Phase 6 work; Folder used
        // to open the Windows folder dialog straight from here, which with the
        // scope locked would have asked for a folder and then thrown the answer
        // away. Both are locked on the card as well, so neither reaches this.
        Workspace.NewChatCommand.Execute(null);
    }

    partial void OnFirstRunChanged(FirstRunViewModel? value) => OnPropertyChanged(nameof(HasFirstRun));

    /// <summary>
    /// Every route that needs a missing model comes here. Until the catalogue
    /// carries download URLs (D7) the first-run installer is the only surface
    /// that can act on it, so it is opened rather than a dead end shown.
    /// </summary>
    private void OpenDownloadManager() => OpenDownloads();

    [RelayCommand]
    /// <summary>
    /// Deleting stored data from Settings leaves the chat behind it holding
    /// rows that no longer exist, so it is read again rather than left showing
    /// a history the database has forgotten.
    /// </summary>
    private async Task ReloadAfterDeletionAsync()
    {
        await Workspace.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        await Memory.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private void CloseDialog() => ActiveDialog = null;

    partial void OnActiveDialogChanged(ConfirmDialogViewModel? value) => OnPropertyChanged(nameof(HasDialog));

    [ObservableProperty]
    public partial SegmentItem SelectedWorkspaceMode { get; set; }

    public WorkspaceMode Mode => (WorkspaceMode)SelectedWorkspaceMode.Value;

    public bool IsMemoryMode => Mode == WorkspaceMode.Memory;

    public bool IsSimpleMode => Mode == WorkspaceMode.Simple;

    public bool IsAdvancedMode => Mode == WorkspaceMode.Advanced;

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRightPanelVisible { get; set; }

    /// <summary>
    /// The chosen project's own name. Null until one is picked, which is what
    /// leaves the selector showing a folder icon and a chevron only.
    /// </summary>
    [ObservableProperty]
    public partial string? ProjectName { get; set; }

    /// <summary>Full path of the chosen project, shown as the selector tooltip.</summary>
    [ObservableProperty]
    public partial string? ProjectPath { get; set; }

    public bool HasProject => ProjectName is not null;

    public string ProjectSelectorLabel => ProjectName ?? "Choose project";

    public string ProjectSelectorTooltip => ProjectPath ?? "Choose project";

    /// <summary>
    /// Which kind of scope the chip is holding. It leads the chip so the path
    /// is not left to say on its own whether it is a repository or one folder
    /// inside one. Empty with no scope, when the chip is not up at all.
    /// </summary>
    public string ScopeKindLabel => Scope switch
    {
        ProjectScopeKind.Project => "Project",
        ProjectScopeKind.Folder => "Folder",
        _ => string.Empty,
    };

    [ObservableProperty]
    public partial string SizeReadout { get; set; } = string.Empty;

    /// <summary>
    /// The size pill is up only during a resize drag. It is raised by
    /// SizeChanged and taken down 400 after the last change.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSizeReadoutVisible { get; set; }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelVisible = !IsRightPanelVisible;

    /// <summary>
    /// The way out of Memory. Without it the only route back to the chat is the
    /// mode slider, which is not where the eye goes from inside a workspace.
    /// </summary>
    [RelayCommand]
    private void BackToChat() => SelectedWorkspaceMode = WorkspaceModes[1];

    /// <summary>
    /// The project selector's menu: Project, Folder or None. Scope defaults to
    /// None and never gates translation.
    ///
    /// Both scopes that pick a path are locked. None is left open because it is
    /// the setting in force and the one the app runs on, so the menu still
    /// states the present setting rather than offering nothing at all.
    /// </summary>
    public IReadOnlyList<ProjectScopeViewModel> ProjectScopes { get; } =
    [
        new(ProjectScopeKind.Project) { IsLocked = true, LockedReason = "Project is coming soon" },
        new(ProjectScopeKind.Folder) { IsLocked = true, LockedReason = "Folder is coming soon" },
        new(ProjectScopeKind.None) { IsCurrent = true },
    ];

    [ObservableProperty]
    public partial ProjectScopeKind Scope { get; set; } = ProjectScopeKind.None;

    partial void OnScopeChanged(ProjectScopeKind value) => OnPropertyChanged(nameof(ScopeKindLabel));

    [ObservableProperty]
    public partial bool IsProjectMenuOpen { get; set; }

    [RelayCommand]
    private void ToggleProjectMenu() => IsProjectMenuOpen = !IsProjectMenuOpen;

    /// <summary>
    /// Picking Project or Folder opens the real Windows folder dialog.
    /// Cancelling it leaves the scope exactly as it was.
    /// </summary>
    [RelayCommand]
    private void ChooseScope(ProjectScopeViewModel choice)
    {
        IsProjectMenuOpen = false;

        // The gate lives here rather than only on the menu row. A disabled row
        // stops a click, which is every route a reader has through the menu, but
        // the command has a second caller in the first-run screen and nothing
        // about a disabled button reaches that one.
        if (choice.IsLocked)
        {
            return;
        }

        if (choice.Kind == ProjectScopeKind.None)
        {
            ProjectName = null;
            ProjectPath = null;
            Scope = ProjectScopeKind.None;
            MarkCurrentScope();
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = choice.Kind == ProjectScopeKind.Project ? "Choose the repository root" : "Choose a folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            // Cancelling changes nothing and opens nothing else.
            return;
        }

        ProjectPath = dialog.FolderName;
        ProjectName = new DirectoryInfo(dialog.FolderName).Name;
        Scope = choice.Kind;
        MarkCurrentScope();
    }

    [RelayCommand]
    private void ClearProject()
    {
        ProjectName = null;
        ProjectPath = null;
        Scope = ProjectScopeKind.None;
        MarkCurrentScope();
    }

    private void MarkCurrentScope()
    {
        foreach (var row in ProjectScopes)
        {
            row.IsCurrent = row.Kind == Scope;
        }
    }

    public void ReportWindowSize(double width, double height)
    {
        SizeReadout = string.Create(
            CultureInfo.CurrentCulture,
            $"{Math.Round(width):0} x {Math.Round(height):0}");

        IsSizeReadoutVisible = true;

        _sizeReadoutTimer.Stop();
        _sizeReadoutTimer.Start();
    }

    partial void OnSelectedWorkspaceModeChanged(SegmentItem value)
    {
        // Advanced opens the right panel; leaving it closes it again.
        IsRightPanelVisible = (WorkspaceMode)value.Value == WorkspaceMode.Advanced;

        // What each entry cost, and what the chat has cost, belong to Advanced.
        // In Simple they would be numbers nobody asked for under every message.
        Workspace.ShowMetrics = (WorkspaceMode)value.Value == WorkspaceMode.Advanced;

        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsMemoryMode));
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
    }

    partial void OnProjectNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectSelectorLabel));
    }

    partial void OnProjectPathChanged(string? value) => OnPropertyChanged(nameof(ProjectSelectorTooltip));
}
