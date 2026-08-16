using System.Collections.ObjectModel;
using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Retrieval;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>One row in the Project sources or Learned rail.</summary>
public sealed record RailRow(string Label, string Count, Brush Dot);

/// <summary>
/// S10's rail and the indexing state behind it. Every count here is read back
/// from the index; none of them is a constant.
/// </summary>
public sealed partial class MemoryViewModel : ObservableObject
{
    private readonly IndexStore _store;
    private readonly IndexingService _indexing;
    private CancellationTokenSource? _running;

    public MemoryViewModel(IndexStore store, IndexingService indexing)
    {
        _store = store;
        _indexing = indexing;

        // The map asks for scopes when its menu opens, and hands back the one
        // that was chosen. The scope travels in that call and nowhere else.
        var resolver = new ReindexScopeResolver(store);
        Map.ScopeResolver = resolver.ResolveAsync;
        Map.ReindexRequested = scope => IndexAsync(scope.ToRequest());
    }

    /// <summary>S12, shown when the Retrieval map tab is chosen.</summary>
    public RetrievalMapViewModel Map { get; } = new();

    /// <summary>
    /// The lookup behind the underlines and the popover. Loaded from the index
    /// on every refresh.
    /// </summary>
    public MemoryService Lookup { get; } = new();

    /// <summary>False on Overview, true on Retrieval map.</summary>
    [ObservableProperty]
    public partial bool IsMapTab { get; set; }

    [RelayCommand]
    private void ShowOverview() => IsMapTab = false;

    [RelayCommand]
    private void ShowMap() => IsMapTab = true;

    partial void OnIsMapTabChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOverviewEmptyState));
        OnPropertyChanged(nameof(ShowOverviewContent));
    }

    public ObservableCollection<RailRow> ProjectSources { get; } = [];

    public ObservableCollection<RailRow> Learned { get; } = [];

    [ObservableProperty]
    public partial IndexTotals Totals { get; set; } = IndexTotals.Empty;

    [ObservableProperty]
    public partial string ProgressActivity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial int QueuedCount { get; set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    /// <summary>Name of the first indexed source, which is what the subhead and the hub carry.</summary>
    [ObservableProperty]
    public partial string? ProjectName { get; set; }

    /// <summary>Set by the shell so the map can gate its focus segments.</summary>
    public int ChatCount { get; set; }

    public bool HasOpenChat { get; set; }

    public bool HasSources => ProjectSources.Count > 0;

    public bool IsEmpty => Totals.IsEmpty;

    /// <summary>The Overview pane's two states, gated on the tab as well.</summary>
    public bool ShowOverviewEmptyState => !IsMapTab && IsEmpty;

    public bool ShowOverviewContent => !IsMapTab && !IsEmpty;

    /// <summary>Which of the two tabs reads as pressed.</summary>
    public bool ShowOverviewTabChecked => !IsMapTab;

    public string SourcesEmptyLine => "Nothing added yet";

    /// <summary>
    /// The state of the index, read off the rail's own rows rather than kept as
    /// a second copy that could disagree with them.
    /// </summary>
    public string IndexedStatus =>
        ProjectSources.Count == 0
            ? "Nothing indexed"
            : ProjectSources.Count == 1 ? "1 source indexed" : $"{ProjectSources.Count} sources indexed";

    public string LearnedEmptyLine => "Nothing learned yet";

    /// <summary>"1 842 pieces ready to search", from the real chunk count.</summary>
    public string TextIndexedLabel => Totals.Chunks.ToString("N0");

    public string QueuedLabel => QueuedCount == 1 ? "1 more queued" : $"{QueuedCount} more queued";

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(true);

        var sources = await _store.GetSourcesAsync(cancellationToken).ConfigureAwait(true);
        Totals = await _store.GetTotalsAsync(cancellationToken).ConfigureAwait(true);

        ProjectSources.Clear();
        foreach (var source in sources)
        {
            ProjectSources.Add(new RailRow(
                source.Name,
                source.ChunkCount == 1 ? "1 chunk" : $"{source.ChunkCount} chunks",
                DotFor(source.Kind)));
        }

        ProjectName = sources.Count > 0 ? sources[0].Name : null;

        Map.Update(ProjectName, sources, Totals, ChatCount, HasOpenChat);

        // Load the index into the lookup so translations can be marked from
        // memory. The lexical path works with no model, so this is live now.
        await Lookup.LoadFromAsync(_store, cancellationToken).ConfigureAwait(true);

        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowOverviewEmptyState));
        OnPropertyChanged(nameof(ShowOverviewContent));
        OnPropertyChanged(nameof(TextIndexedLabel));
    }

    /// <summary>
    /// Runs a request off the UI thread, marshalling progress back. The scope
    /// travels in the request rather than in shared state.
    /// </summary>
    public async Task IndexAsync(IndexingRequest request)
    {
        _running?.Cancel();
        _running = new CancellationTokenSource();

        IsIndexing = true;

        var progress = new Progress<IndexingProgress>(report =>
        {
            ProgressActivity = report.Activity;
            ProgressPercent = report.Percent;
            QueuedCount = report.Queued;
            OnPropertyChanged(nameof(QueuedLabel));

            // Every step shows up in the live activity panel as it happens.
            if (report.Activity.StartsWith("Parsing", StringComparison.Ordinal))
            {
                Map.Log(ActivityRowViewModel.Indexing(
                    report.Activity,
                    $"file.parsed - {report.Queued} queued"));
            }
            else if (report.IsFinished && !report.WasCancelled)
            {
                Map.Log(ActivityRowViewModel.Done(
                    report.Activity,
                    $"batch.summary - {report.ChunksWritten} chunks - {report.WordsRead} words"));
            }

            if (report.IsFinished)
            {
                IsIndexing = false;
            }
        });

        // Task.Run keeps the file and database work off the UI thread; the
        // Progress callback marshals each report back to it.
        await Task.Run(
            () => _indexing.IndexAsync(request, progress, _running.Token),
            _running.Token).ConfigureAwait(true);

        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelIndexing() => _running?.Cancel();

    private static Brush DotFor(SourceKind kind) => kind switch
    {
        SourceKind.Repository => Tokens.Get<Brush>("BrushRepoSource"),
        SourceKind.Chat => Tokens.Get<Brush>("BrushChatSource"),
        SourceKind.PastedNote => Tokens.Get<Brush>("BrushWarn"),
        _ => Tokens.Get<Brush>("BrushAccent"),
    };
}
