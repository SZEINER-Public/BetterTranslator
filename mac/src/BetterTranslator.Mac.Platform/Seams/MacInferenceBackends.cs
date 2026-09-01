using BetterTranslator.Core.Models;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacInferenceBackends : IInferenceBackendSeam
{
    private static readonly RuntimeBackend[] OnlyBackend = [RuntimeBackend.Cpu];

    public IReadOnlyList<RuntimeBackend> Available => OnlyBackend;

    public RuntimeBackend Selected => RuntimeBackend.Cpu;

    public string SelectedTitle => "Processor";

    public string SelectedSummary => "Translation runs on this Mac's processor cores.";
}
