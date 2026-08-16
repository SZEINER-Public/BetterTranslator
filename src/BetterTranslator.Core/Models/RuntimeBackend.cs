namespace BetterTranslator.Core.Models;

/// <summary>
/// Which BetterRuntime flavour runs the model. One self-contained DLL per
/// entry, so the choice is a file on disk rather than a switch inside one
/// library.
///
/// Declared here rather than beside the runtime code because it is a persisted
/// setting, and Core cannot reference Runtime without a cycle.
/// </summary>
public enum RuntimeBackend
{
    /// <summary>Works everywhere. Roughly 10 tokens a second on this class of machine.</summary>
    Cpu,

    /// <summary>Cross-vendor GPU acceleration: AMD, Intel and NVIDIA all speak it.</summary>
    Vulkan,

    /// <summary>NVIDIA only.</summary>
    Cuda,
}
