using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Adapters;

public sealed class MacRuntimeSurface(RuntimeStatusViewModel status, IInferenceBackendSeam backends)
{
    public RuntimeStatusViewModel Status => status;

    public string RuntimeTitle => backends.SelectedTitle;

    public string RuntimeSummary => backends.SelectedSummary;

    public bool HasChoice => backends.Available.Count > 1;
}
