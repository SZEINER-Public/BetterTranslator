using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Adapters;

public sealed class MacSettingsSurface(SettingsViewModel settings, IInferenceBackendSeam backends, IUpdateTriggerSeam updates)
{
    public SettingsViewModel Settings => settings;

    public string RuntimeTitle => backends.SelectedTitle;

    public string RuntimeSummary => backends.SelectedSummary;

    public bool HasRuntimeChoice => backends.Available.Count > 1;

    public bool UpdatesSupported => updates.IsSupported;

    public string UpdatesUnsupportedReason => updates.UnsupportedReason;
}
