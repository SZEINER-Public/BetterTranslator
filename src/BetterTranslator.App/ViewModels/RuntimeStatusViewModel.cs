using System.IO;
using System.Windows;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Inference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// The runtime, made visible and controllable.
///
/// Loading a model costs seconds and gigabytes and happens on the first send, so
/// until now the only way to find out whether it had worked was to send
/// something and wait. This starts it on demand, stops it, and says what it is
/// doing while it does it.
///
/// "Pause" is the icon, not the mechanism. The runtime has no suspend: a model
/// is resident or it is not, so stopping unloads it and gives the memory back,
/// and the next send loads it again. The panel says so rather than implying a
/// resume that does not exist.
/// </summary>
public sealed partial class RuntimeStatusViewModel : ObservableObject
{
    private readonly LocalTranslator _translator;
    private readonly Func<string> _modelPath;
    private readonly Func<RuntimeBackend> _backend;

    public RuntimeStatusViewModel(
        LocalTranslator translator,
        Func<string> modelPath,
        Func<RuntimeBackend> backend)
    {
        _translator = translator;
        _modelPath = modelPath;
        _backend = backend;

        // Raised from whatever thread loaded the model or ran the generation,
        // which is never the UI one. Marshalled here rather than at each call
        // site; outside a running application there is no dispatcher and the
        // direct call is correct.
        //
        // BeginInvoke, never Invoke. A notification must not block the thread
        // that raised it: Invoke waits for the UI thread to run the callback,
        // and the loading thread ended up waiting on a dispatcher that was not
        // pumping -- which hung the whole test suite rather than the four
        // seconds it usually takes.
        _translator.Changed += () =>
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null || dispatcher.CheckAccess())
            {
                RaiseState();
            }
            else
            {
                dispatcher.BeginInvoke(RaiseState);
            }
        };
    }

    [ObservableProperty]
    public partial bool IsPanelOpen { get; set; }

    [ObservableProperty]
    public partial string? SelectionNote { get; set; }

    public bool HasSelectionNote => !string.IsNullOrWhiteSpace(SelectionNote);

    partial void OnSelectionNoteChanged(string? value) => OnPropertyChanged(nameof(HasSelectionNote));

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    public TranslatorState State => _translator.State;

    public bool IsReady => State == TranslatorState.Ready;

    public bool IsStarting => State == TranslatorState.Starting;

    public bool HasFailed => State == TranslatorState.Failed;

    /// <summary>A translation is running right now, so there is something to stop.</summary>
    public bool IsGenerating => _translator.IsGenerating;

    public bool CanStart => !IsReady && !IsStarting && HasModel;

    /// <summary>
    /// Pause acts on the run, not on the model. There is nothing to pause when
    /// nothing is generating, and stopping a run leaves the model resident so
    /// the next send starts at once.
    /// </summary>
    public bool CanPause => IsGenerating;

    /// <summary>
    /// Unload acts on the model. Separate from pause because they are separate
    /// things: one abandons the answer being written, the other gives back the
    /// gigabytes the weights are holding.
    /// </summary>
    public bool CanUnload => IsReady || IsStarting;

    public bool HasModel => _modelPath().Length > 0 && File.Exists(_modelPath());

    /// <summary>
    /// One line for the button's tooltip and the panel's heading, naming the
    /// state rather than colouring a dot and leaving it to be guessed.
    /// </summary>
    public string StateLabel => IsGenerating
        ? "Translating"
        : State switch
        {
            TranslatorState.Starting => "Loading the model",
            TranslatorState.Ready => "Ready",
            TranslatorState.Failed => "Could not start",
            _ => HasModel ? "Not loaded" : "No model",
        };

    public string StateDetail => IsGenerating
        ? "Generating an answer. Pause abandons this one; the model stays loaded."
        : State switch
        {
            TranslatorState.Starting => "Reading the weights off disk. This is seconds, not milliseconds.",
            TranslatorState.Ready => "The model is resident and the next translation starts at once.",
            TranslatorState.Failed => _translator.Reason ?? "The runtime did not start.",
            _ when !HasModel => "Nothing is selected to load. Install a model from Models and runtimes.",
            _ => "Nothing is loaded yet. The first translation loads it, or start it here.",
        };

    private readonly Engine.Languages.ModelPrompts _prompts = new();

    /// <summary>
    /// The model id the prompt registry matches against. The file name carries
    /// it -- "translategemma-4b-it.Q4_K_M" contains "translategemma" -- which is
    /// how the reference engine matches too: a substring of the served id, never
    /// a path built from a catalogue id.
    /// </summary>
    private string ModelId => Path.GetFileNameWithoutExtension(_modelPath());

    public bool HasTrainedPrompt => _prompts.HasTrainedShape(ModelId);

    /// <summary>
    /// The shape this model's template declares, read from its own GGUF header,
    /// or null when it declares one that cannot be rendered here.
    /// </summary>
    private Engine.Models.ChatTemplate? Template =>
        HasModel ? Engine.Models.ChatTemplate.For(_modelPath()) : null;

    /// <summary>
    /// "Gemma / TranslateGemma", "ChatML / engine", or the runtime's own. Two
    /// halves because they can be right and wrong independently: the markers
    /// come from the model's file and the instruction from the prompt registry.
    /// </summary>
    public string PromptShapeLabel
    {
        get
        {
            if (!HasModel)
            {
                return "none";
            }

            if (Template is null)
            {
                return "runtime default";
            }

            return $"{Template.Name} / {(_prompts.For(ModelId)?.Label ?? "engine")}";
        }
    }

    /// <summary>
    /// Said only when the model really is being asked in a way it was never
    /// tuned for -- which now means only when its template cannot be rendered.
    ///
    /// A model with no published prompt is no longer a problem: it gets the
    /// engine's own instruction inside its own turn markers, which is exactly
    /// what the reference engine does. What is still a problem is a template
    /// this build does not recognise, because then the runtime's one fixed
    /// prompt is used and both halves belong to another model.
    /// </summary>
    public string? PromptWarning
    {
        get
        {
            if (!HasModel || Template is not null)
            {
                return null;
            }

            return $"{ModelLabel} declares a chat template this build cannot render, so the runtime's fixed "
                + "TranslateGemma prompt is used. Translations will be usable but not what this model was tuned for.";
        }
    }

    public bool HasPromptWarning => PromptWarning is not null;

    /// <summary>
    /// The flavour that is actually running, not the one that was chosen.
    ///
    /// These are different facts and they came apart in the field: a CUDA build
    /// installed without the cuBLAS libraries beside it cannot load, the loader
    /// quietly falls back, and this panel went on reporting "NVIDIA CUDA" while
    /// every token came off the processor. The only symptom was that it was
    /// slow. Reading the chosen value here is what made that invisible.
    /// </summary>
    public string BackendLabel => LocalTranslator.LoadedFlavor switch
    {
        "BetterRuntimeCUDA" => "NVIDIA CUDA",
        "BetterRuntimeVulkan" => "AMD Vulkan",
        "BetterRuntimeCPU" => "CPU",

        // Nothing has loaded yet, so the choice is the honest answer: it is what
        // will be tried, and saying nothing at all would be worse.
        _ => _backend() switch
        {
            RuntimeBackend.Cuda => "NVIDIA CUDA",
            RuntimeBackend.Vulkan => "AMD Vulkan",
            _ => "CPU",
        },
    };

    /// <summary>
    /// Said out loud when what is running is not what was asked for, naming the
    /// reason. A silent substitution is the defect this exists to prevent.
    /// </summary>
    public string? BackendNote => LocalTranslator.FlavorNote;

    public bool HasBackendNote => BackendNote is not null;

    /// <summary>
    /// The model actually loaded where there is one, and the one that would be
    /// loaded where there is not. Two different facts, so the label says which.
    /// </summary>
    public string ModelLabel =>
        _translator.LoadedModelName
        ?? (HasModel ? Path.GetFileNameWithoutExtension(_modelPath()) : "none");

    /// <summary>
    /// What the last translation cost. Set by the shell, because the translator
    /// reports it per call and this is the only place it survives afterwards.
    /// </summary>
    [ObservableProperty]
    public partial string? LastRun { get; set; }

    public bool HasLastRun => !string.IsNullOrEmpty(LastRun);

    partial void OnLastRunChanged(string? value) => OnPropertyChanged(nameof(HasLastRun));

    [ObservableProperty]
    public partial string? ConfigAdvisories { get; set; }

    public bool HasConfigAdvisories => !string.IsNullOrEmpty(ConfigAdvisories);

    partial void OnConfigAdvisoriesChanged(string? value) => OnPropertyChanged(nameof(HasConfigAdvisories));

    /// <summary>
    /// Loads now rather than on the next send. The panel opens itself, because
    /// pressing start and being shown nothing is indistinguishable from pressing
    /// a button that did nothing.
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        IsPanelOpen = true;
        RaiseState();

        await _translator.EnsureLoadedAsync(_modelPath(), CancellationToken.None).ConfigureAwait(true);

        RaiseState();
    }

    /// <summary>
    /// Abandons the translation in flight. The model stays where it is: this is
    /// "stop writing this answer", not "put the model down".
    /// </summary>
    [RelayCommand]
    private void Pause()
    {
        _translator.CancelGeneration();
        RaiseState();
    }

    /// <summary>
    /// Puts the model down and gives its memory back. The next send loads it
    /// again, which costs the same seconds the first one did -- so this is worth
    /// pressing to reclaim gigabytes, not to tidy up between translations.
    /// </summary>
    [RelayCommand]
    private async Task UnloadAsync()
    {
        await _translator.UnloadAsync().ConfigureAwait(true);
        LastRun = null;
        RaiseState();
    }

    public void RaiseState()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(IsGenerating));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanUnload));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(BackendLabel));
        OnPropertyChanged(nameof(BackendNote));
        OnPropertyChanged(nameof(HasBackendNote));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(HasTrainedPrompt));
        OnPropertyChanged(nameof(PromptShapeLabel));
        OnPropertyChanged(nameof(PromptWarning));
        OnPropertyChanged(nameof(HasPromptWarning));

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        UnloadCommand.NotifyCanExecuteChanged();
    }
}
