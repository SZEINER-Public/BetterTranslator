using BetterTranslator.Indexing.Index;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// One row in the Pick sources stage: an indexed source with its real chunk
/// count, tickable.
/// </summary>
public sealed partial class PickableSourceViewModel(IndexedSource source) : ObservableObject
{
    public IndexedSource Source { get; } = source;

    public string Name => Source.Name;

    /// <summary>The count that is actually stored, not an estimate.</summary>
    public string ChunkLabel => Source.ChunkCount == 1 ? "1 chunk" : $"{Source.ChunkCount} chunks";

    [ObservableProperty]
    public partial bool IsPicked { get; set; }
}
