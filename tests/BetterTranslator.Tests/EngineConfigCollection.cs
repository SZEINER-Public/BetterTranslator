using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Serialises the test classes that read or write the engine's configuration.
///
/// <c>ConfigStore.Active</c> and the per-language <c>RagConfig</c> cache are
/// static by design -- the registries are constructed in a dozen places and the
/// store is chosen once at startup. xUnit runs test CLASSES in parallel, so a
/// class that points the store at a temp folder and one that asserts what is in
/// force by default will fail each other roughly one run in three, and which one
/// fails depends on scheduling.
///
/// A collection is the fix rather than a lock, because the contention is between
/// whole classes -- one of them holds its config in a static field initialiser
/// that runs at class load, which no lock inside a test body can cover.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EngineConfigCollection
{
    public const string Name = "engine-config";
}
