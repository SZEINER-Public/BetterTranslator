using System.Collections.ObjectModel;
using BetterTranslator.App.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Retrieval;
using BetterTranslator.Map.World;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public enum MapFocus
{
    WholeProject,
    AllChats,
    ThisChat,
}

/// <summary>
/// S12. Owns the world, the focus control and the stats strip. A focus whose
/// material does not exist is locked with its own stated reason rather than
/// hidden, and if the active focus loses its material the map moves to the
/// first focus that has any.
/// </summary>
public sealed partial class RetrievalMapViewModel : ObservableObject
{
    public RetrievalMapViewModel()
    {
        FocusSegments =
        [
            new SegmentItem
            {
                Value = MapFocus.WholeProject,
                Label = "Whole project",
                Width = 116,
            },
            new SegmentItem
            {
                Value = MapFocus.AllChats,
                Label = "All chats",
                Width = 86,
            },
            new SegmentItem
            {
                Value = MapFocus.ThisChat,
                Label = "This chat",
                Width = 86,
            },
        ];

        SelectedFocus = FocusSegments[0];
    }

    public ObservableCollection<SegmentItem> FocusSegments { get; }

    /// <summary>The four reindex scopes, resolved when the menu opens.</summary>
    public ObservableCollection<ReindexScope> ReindexScopes { get; } = [];

    /// <summary>The Pick sources second stage: one row per indexed source.</summary>
    public ObservableCollection<PickableSourceViewModel> PickableSources { get; } = [];

    [ObservableProperty]
    public partial bool IsReindexMenuOpen { get; set; }

    /// <summary>True once Pick sources has been chosen, which swaps the stage.</summary>
    [ObservableProperty]
    public partial bool IsPickingSources { get; set; }

    public int PickedCount => PickableSources.Count(s => s.IsPicked);

    public string PickedLabel => PickedCount == 1 ? "1 source" : $"{PickedCount} sources";

    public bool CanReindexPicked => PickedCount > 0;

    /// <summary>Runs the scope the user chose. Set by the shell.</summary>
    public Func<ReindexScope, Task>? ReindexRequested { get; set; }

    /// <summary>Set by the shell so the scopes resolve against the live index.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ReindexScope>>>? ScopeResolver { get; set; }

    [RelayCommand]
    private async Task OpenReindexMenuAsync()
    {
        IsPickingSources = false;

        if (ScopeResolver is not null)
        {
            var scopes = await ScopeResolver(CancellationToken.None).ConfigureAwait(true);

            ReindexScopes.Clear();
            foreach (var scope in scopes)
            {
                ReindexScopes.Add(scope);
            }
        }

        IsReindexMenuOpen = true;
    }

    [RelayCommand]
    private async Task ChooseScopeAsync(ReindexScope scope)
    {
        if (!scope.IsAvailable)
        {
            return;
        }

        // Pick sources opens a second stage rather than starting a run.
        if (scope.Kind == ReindexScopeKind.PickedSources)
        {
            PickableSources.Clear();
            foreach (var source in scope.Sources)
            {
                var row = new PickableSourceViewModel(source);
                row.PropertyChanged += (_, _) => RaisePicked();
                PickableSources.Add(row);
            }

            RaisePicked();
            IsPickingSources = true;
            return;
        }

        IsReindexMenuOpen = false;

        if (ReindexRequested is not null)
        {
            await ReindexRequested(scope).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void BackToScopes() => IsPickingSources = false;

    [RelayCommand]
    private async Task ReindexPickedAsync()
    {
        if (!CanReindexPicked)
        {
            return;
        }

        // A fresh scope built from the picks, so nothing from the menu's
        // resolved state travels further than this call.
        var scope = new ReindexScope
        {
            Kind = ReindexScopeKind.PickedSources,
            Sources = [.. PickableSources.Where(s => s.IsPicked).Select(s => s.Source)],
        };

        IsReindexMenuOpen = false;
        IsPickingSources = false;

        if (ReindexRequested is not null)
        {
            await ReindexRequested(scope).ConfigureAwait(true);
        }
    }

    private void RaisePicked()
    {
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(PickedLabel));
        OnPropertyChanged(nameof(CanReindexPicked));
    }

    [ObservableProperty]
    public partial SegmentItem SelectedFocus { get; set; }

    [ObservableProperty]
    public partial MapWorld World { get; set; } = new();

    [ObservableProperty]
    public partial IndexTotals Totals { get; set; } = IndexTotals.Empty;

    /// <summary>Non-null while the pointer is over a node.</summary>
    [ObservableProperty]
    public partial MapNode? HoveredNode { get; set; }

    [ObservableProperty]
    public partial string StatusLine { get; set; } = "Idle";

    /// <summary>
    /// The live activity panel. Newest first, and bounded so a long session
    /// does not grow without limit.
    /// </summary>
    public ObservableCollection<ActivityRowViewModel> Activity { get; } = [];

    private const int MaxActivityRows = 200;

    public bool HasActivity => Activity.Count > 0;

    public string ActivityEmptyLine => "Nothing indexed, so nothing is being read.";

    /// <summary>Adds a row at the top, trimming the tail.</summary>
    public void Log(ActivityRowViewModel row)
    {
        Activity.Insert(0, row);

        while (Activity.Count > MaxActivityRows)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }

        OnPropertyChanged(nameof(HasActivity));
    }

    /// <summary>Logs everything a lookup did, oldest first so it reads in order.</summary>
    public void LogLookup(MemoryLookup lookup)
    {
        foreach (var activity in lookup.Events)
        {
            Log(ActivityRowViewModel.From(activity));
        }
    }

    public bool IsEmpty => World.IsEmpty;

    /// <summary>Raised when the view should re-frame against its live size.</summary>
    public event Action? FitRequested;

    /// <summary>Raised when the canvas needs repainting.</summary>
    public event Action? RedrawRequested;

    public string TextIndexedValue => Totals.Chunks.ToString("N0");

    public string LookupsValue => "0";

    public string ContextUsedValue => "0";

    /// <summary>
    /// Rebuilds the world from what is indexed and re-gates the focus control.
    /// </summary>
    public void Update(string? projectName, IReadOnlyList<IndexedSource> sources, IndexTotals totals, int chatCount, bool hasOpenChat)
    {
        Totals = totals;

        var mapSources = sources
            .Select(s => new MapSource(s.Name, s.ChunkCount, ZoneFor(s.Kind)))
            .ToList();

        World = MapWorldBuilder.Build(projectName, mapSources, totals.Chunks);

        GateFocus(sources, chatCount, hasOpenChat);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TextIndexedValue));

        RedrawRequested?.Invoke();
        FitRequested?.Invoke();
    }

    /// <summary>
    /// Locks each focus whose material does not exist, and states why on the
    /// segment itself.
    /// </summary>
    private void GateFocus(IReadOnlyList<IndexedSource> sources, int chatCount, bool hasOpenChat)
    {
        var hasNonChatSource = sources.Any(s => s.Kind != SourceKind.Chat);

        Lock(MapFocus.WholeProject, !hasNonChatSource,
            "Nothing but chats is indexed - add a folder, repository or files");

        Lock(MapFocus.AllChats, chatCount == 0,
            "No chats yet - translate something first");

        Lock(MapFocus.ThisChat, !hasOpenChat,
            "No chat open yet - translate something first");

        // If the focus that was active has lost its material, move to the first
        // one that still has any.
        if (SelectedFocus.IsLocked)
        {
            var open = FocusSegments.FirstOrDefault(s => !s.IsLocked);
            if (open is not null)
            {
                SelectedFocus = open;
            }
        }
    }

    private void Lock(MapFocus focus, bool locked, string reason)
    {
        var segment = FocusSegments.First(s => (MapFocus)s.Value == focus);
        segment.IsLocked = locked;
        segment.LockedReason = locked ? reason : null;
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    private static MapZone ZoneFor(SourceKind kind) => kind switch
    {
        SourceKind.Chat => MapZone.Chats,
        SourceKind.PastedNote => MapZone.ProjectMemory,
        _ => MapZone.ProjectFiles,
    };
}
