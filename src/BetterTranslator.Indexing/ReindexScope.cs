using BetterTranslator.Indexing.Index;

namespace BetterTranslator.Indexing;

public enum ReindexScopeKind
{
    /// <summary>Everything currently indexed.</summary>
    Everything,

    /// <summary>Only sources that are new or whose file has changed.</summary>
    OnlyWhatChanged,

    /// <summary>A hand-picked subset of what is already indexed.</summary>
    PickedSources,

    /// <summary>Chat sources and learned memory.</summary>
    JustTheChats,
}

/// <summary>
/// One reindex scope, resolved against what is actually indexed. The resolved
/// source list travels inside the request, so a scope can never leak into a
/// later pass through shared state.
/// </summary>
public sealed record ReindexScope
{
    public required ReindexScopeKind Kind { get; init; }

    /// <summary>The sources this scope will actually reindex.</summary>
    public required IReadOnlyList<IndexedSource> Sources { get; init; }

    public int Count => Sources.Count;

    /// <summary>Menu label.</summary>
    public string Title => Kind switch
    {
        ReindexScopeKind.Everything => "Everything",
        ReindexScopeKind.OnlyWhatChanged => "Only what changed",
        ReindexScopeKind.PickedSources => "Pick sources...",
        _ => "Just the chats",
    };

    /// <summary>
    /// The line under the label, always a real count of what this scope covers.
    /// </summary>
    public string Detail => Kind switch
    {
        ReindexScopeKind.Everything =>
            Count == 1 ? "1 source currently indexed" : $"{Count} sources currently indexed",

        ReindexScopeKind.OnlyWhatChanged => Count == 0
            ? "Nothing has changed"
            : Count == 1 ? "1 new or edited since the last run" : $"{Count} new or edited since the last run",

        ReindexScopeKind.PickedSources => "Choose from what is already indexed",

        _ => Count == 1 ? "1 chat source and learned memory" : $"{Count} chat sources and learned memory",
    };

    /// <summary>
    /// Disabled when there is nothing for it to do, with the reason stated in
    /// the detail line rather than left to a grey row.
    /// </summary>
    public bool IsAvailable => Kind switch
    {
        ReindexScopeKind.PickedSources => Count > 0,
        _ => Count > 0,
    };

    /// <summary>True for the scope that opens a second stage rather than running.</summary>
    public bool IsPickStage => Kind == ReindexScopeKind.PickedSources;

    /// <summary>The log's opening line for a run in this scope.</summary>
    public string OpeningLogLine(int totalIndexed) => Kind switch
    {
        ReindexScopeKind.Everything => $"Index run started - {Count} of {totalIndexed} sources current",
        ReindexScopeKind.OnlyWhatChanged => $"Index run started - {Count} changed of {totalIndexed}",
        ReindexScopeKind.PickedSources => $"Index run started - {Count} picked of {totalIndexed}",
        _ => $"Index run started - {Count} chat sources of {totalIndexed}",
    };

    /// <summary>
    /// Turns the scope into a request. Replace is set so a reindex refreshes
    /// what it covers rather than duplicating it.
    /// </summary>
    public IndexingRequest ToRequest() => new()
    {
        Folders = [],
        Files = [.. Sources.Select(s => s.Location)],
        Replace = false,
    };
}

/// <summary>
/// Resolves each scope against the index at the moment the menu opens, so the
/// counts a user reads are the counts the run will use.
/// </summary>
public sealed class ReindexScopeResolver(IndexStore store)
{
    public async Task<IReadOnlyList<ReindexScope>> ResolveAsync(CancellationToken cancellationToken)
    {
        var sources = await store.GetSourcesAsync(cancellationToken).ConfigureAwait(false);

        var changed = sources.Where(HasChangedOnDisk).ToList();
        var chats = sources.Where(s => s.Kind == SourceKind.Chat).ToList();

        return
        [
            new ReindexScope { Kind = ReindexScopeKind.Everything, Sources = sources },
            new ReindexScope { Kind = ReindexScopeKind.OnlyWhatChanged, Sources = changed },
            new ReindexScope { Kind = ReindexScopeKind.PickedSources, Sources = sources },
            new ReindexScope { Kind = ReindexScopeKind.JustTheChats, Sources = chats },
        ];
    }

    /// <summary>
    /// A source counts as changed when its file is gone or its content no
    /// longer hashes to what was stored.
    /// </summary>
    public static bool HasChangedOnDisk(IndexedSource source)
    {
        if (source.Kind == SourceKind.Chat)
        {
            return false;
        }

        if (!File.Exists(source.Location))
        {
            return true;
        }

        if (source.ContentHash is null)
        {
            return true;
        }

        try
        {
            return ContentHash.Of(File.ReadAllText(source.Location)) != source.ContentHash;
        }
        catch (IOException)
        {
            return false;
        }
    }

}
