using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One effort, and whether its model is actually present. Choosing an effort
/// whose model is missing does not switch: it opens the download manager, and
/// the item says which model is missing.
/// </summary>
public sealed partial class EffortOptionViewModel(EffortTier tier, ModelComponent model) : ObservableObject
{
    public EffortTier Tier { get; } = tier;

    public TranslationEffort Effort => Tier.Effort;

    public ModelComponent Model { get; } = model;

    public string Label => Tier.Label;

    /// <summary>The model note under the label, as the effort menu shows it.</summary>
    public string Note => Model.Name;

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    /// <summary>
    /// True for the option in force. The radio needs it: without it the card
    /// draws an empty ring whichever model is actually selected.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    public bool IsBlocked => !IsInstalled;

    /// <summary>Shown beside a blocked item instead of letting it look available.</summary>
    public string? InstallHint => IsInstalled ? null : "Install";

    /// <summary>Names the missing model rather than saying it is unavailable.</summary>
    public string? BlockedTooltip => IsInstalled ? null : $"{Model.Name} is not installed";

    /// <summary>
    /// The Advanced radio's note when its model is absent, naming the effort it
    /// would enable.
    /// </summary>
    public string? RadioNote => IsInstalled
        ? null
        : $"Not installed. Download it to use {Tier.Label}.";

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(InstallHint));
        OnPropertyChanged(nameof(BlockedTooltip));
        OnPropertyChanged(nameof(RadioNote));
    }
}

/// <summary>
/// S8. The composer carries the effort menu; Advanced carries the model radios,
/// temperature and user prompt. Advanced adds no mode chips to the composer and
/// no context field.
/// </summary>
public sealed partial class TranslationSettingsViewModel : ObservableObject
{
    private const double DefaultTemperature = 0.2;

    private readonly InstallPaths _paths;
    private readonly Action _openDownloadManager;

    public TranslationSettingsViewModel(InstallPaths paths, Action openDownloadManager)
    {
        _paths = paths;
        _openDownloadManager = openDownloadManager;

        Efforts =
        [
            .. EffortTiers.All.Select(tier => new EffortOptionViewModel(
                tier,
                ComponentCatalog.BuiltIn.Single(c => c.Id == tier.ModelId))),
        ];

        RefreshInstalled();
    }

    public IReadOnlyList<EffortOptionViewModel> Efforts { get; }

    [ObservableProperty]
    public partial EffortOptionViewModel? SelectedEffort { get; set; }

    /// <summary>Which effort is in force, as the settings row stores it.</summary>
    public TranslationEffort Effort => SelectedEffort?.Effort ?? EffortTiers.Default;

    public string EffortLabel => SelectedEffort?.Label ?? EffortTiers.NothingInstalledLabel;

    public bool HasSelectedEffort => SelectedEffort is not null;

    [ObservableProperty]
    public partial string? SelectionNotice { get; set; }

    /// <summary>
    /// The model this effort translates with. This is what makes Simple and
    /// Thinking mean something: they are not two labels over one model, they
    /// select which GGUF gets loaded.
    /// </summary>
    public ModelComponent Model =>
        (SelectedEffort ?? Efforts.Single(option => option.Effort == EffortTiers.Default)).Model;

    /// <summary>
    /// Restores the stored choice at launch. The resolver decides whether it
    /// still stands: an effort whose model has since been deleted would load
    /// nothing and every send would report no model.
    /// </summary>
    public void Restore(TranslationEffort effort)
    {
        _chosen = effort;
        RefreshInstalled();
    }

    /// <summary>
    /// Restores what Advanced was left showing. These used to reset on every
    /// launch, which was survivable while the window was the only thing that
    /// read them; an agent translating through this application sends what is
    /// stored, so a slider that forgets means the two send different prompts.
    ///
    /// Silent: restoring is not a change, and raising here would write the value
    /// straight back over the one just read.
    /// </summary>
    public void RestoreAdvanced(double temperature, string instruction)
    {
        _restoring = true;

        try
        {
            Temperature = temperature;
            UserPrompt = instruction;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Raised when the reader moves the slider, so the shell can store it.</summary>
    public event Action<double>? TemperatureChanged;

    /// <summary>Raised when the reader edits the standing instruction.</summary>
    public event Action<string>? InstructionChanged;

    private bool _restoring;

    private bool _resolving;

    private TranslationEffort? _chosen;

    /// <summary>Raised when the effort changes, so the shell can store it.</summary>
    public event Action<TranslationEffort>? EffortChanged;

    public event Action<TranslationEffort>? SelectionResolved;

    partial void OnSelectedEffortChanged(EffortOptionViewModel? value)
    {
        MarkChosen();
        OnPropertyChanged(nameof(Effort));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(EffortLabel));
        OnPropertyChanged(nameof(HasSelectedEffort));

        // Whether the slider does anything is a fact about the model, so it moves
        // when the model does.
        OnPropertyChanged(nameof(TemperatureIsAdjustable));
        OnPropertyChanged(nameof(TemperatureNote));

        if (_resolving)
        {
            SelectionResolved?.Invoke(Effort);
            return;
        }

        _chosen = value?.Effort;
        EffortChanged?.Invoke(Effort);
    }

    /// <summary>One option carries the mark, so the cards cannot both look on.</summary>
    private void MarkChosen()
    {
        foreach (var option in Efforts)
        {
            option.IsChosen = ReferenceEquals(option, SelectedEffort);
        }
    }

    [ObservableProperty]
    public partial bool IsEffortMenuOpen { get; set; }

    /// <summary>Low keeps wording close to the source.</summary>
    [ObservableProperty]
    public partial double Temperature { get; set; } = DefaultTemperature;

    partial void OnTemperatureChanged(double value)
    {
        OnPropertyChanged(nameof(TemperatureReadout));

        if (!_restoring)
        {
            TemperatureChanged?.Invoke(value);
        }
    }

    [ObservableProperty]
    public partial string UserPrompt { get; set; } = string.Empty;

    partial void OnUserPromptChanged(string value)
    {
        if (!_restoring)
        {
            InstructionChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Whether the chosen model will use this at all.
    ///
    /// A model whose card publishes a greedy example has its whole sampler
    /// written over before the request goes out, temperature included. The
    /// slider stayed live and showed the reader's own figure while zero went
    /// out, which is a control that lies about what it does. It is locked
    /// instead, and the note beside it says why: a disabled control with no
    /// reason is the same defect in the other direction.
    /// </summary>
    public bool TemperatureIsAdjustable =>
        !Runtime.Verification.SamplerConfigGuard.AppliesTo(Model.FileName);

    public string TemperatureNote =>
        TemperatureIsAdjustable
            ? "Low keeps wording close to the source."
            : $"{Model.Name} runs greedy. It sends 0.00 whatever this says, so this applies to the other model.";

    /// <summary>
    /// The value shown beside the label. Invariant, so the decimal point is a
    /// dot: this is an engine parameter read back to whoever set it, not a
    /// quantity being reported in the reader's own locale.
    /// </summary>
    public string TemperatureReadout =>
        Temperature.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string UserPromptPlaceholder =>
        "Sent with every request. Example: keep product names in English.";

    /// <summary>Re-reads what is on disk, so removing a model blocks its routes.</summary>
    public void RefreshInstalled()
    {
        foreach (var effort in Efforts)
        {
            effort.IsInstalled = _paths.IsInstalled(effort.Model);
        }

        ResolveSelection();
    }

    private void ResolveSelection()
    {
        var selection = EffortTiers.Resolve(
            Efforts.Where(option => option.IsInstalled).Select(option => option.Tier.ModelId),
            _chosen);

        SelectionNotice = selection.Notice;

        var wanted = selection.Selected is null
            ? null
            : Efforts.Single(option => option.Effort == selection.Selected.Effort);

        if (ReferenceEquals(wanted, SelectedEffort))
        {
            MarkChosen();
            return;
        }

        _resolving = true;

        try
        {
            SelectedEffort = wanted;
        }
        finally
        {
            _resolving = false;
        }
    }

    [RelayCommand]
    private void ToggleEffortMenu() => IsEffortMenuOpen = !IsEffortMenuOpen;

    /// <summary>
    /// Choosing an effort whose model is missing does not switch; it routes to
    /// the download manager instead.
    /// </summary>
    [RelayCommand]
    private void ChooseEffort(EffortOptionViewModel option)
    {
        IsEffortMenuOpen = false;

        if (option.IsBlocked)
        {
            _openDownloadManager();
            return;
        }

        _chosen = option.Effort;
        SelectedEffort = option;
    }

    /// <summary>The Get it button beside a blocked radio.</summary>
    [RelayCommand]
    private void GetModel(EffortOptionViewModel option)
    {
        _ = option;
        _openDownloadManager();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        Temperature = DefaultTemperature;
        UserPrompt = string.Empty;

        _chosen = null;
        ResolveSelection();

        EffortChanged?.Invoke(Effort);
    }
}
