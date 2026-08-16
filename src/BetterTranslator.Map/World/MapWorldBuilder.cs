using SkiaSharp;

namespace BetterTranslator.Map.World;

/// <summary>What the map needs to know about one indexed source.</summary>
public sealed record MapSource(string Name, int ChunkCount, MapZone Zone);

/// <summary>
/// Lays the graph out from what is actually indexed. The hub is named after the
/// source: the project name for a folder, the repository name for a repository,
/// "No project" when only loose files are indexed.
/// </summary>
public static class MapWorldBuilder
{
    private static readonly SKPoint Hub = new(1180, 850);

    public static MapWorld Build(string? projectName, IReadOnlyList<MapSource> sources, int totalChunks)
    {
        var world = new MapWorld();

        if (sources.Count == 0)
        {
            return world;
        }

        var hubId = Guid.NewGuid();

        world.Nodes.Add(new MapNode
        {
            Id = hubId,
            Label = projectName ?? "No project",
            Note = BuildNote(sources.Count, totalChunks),
            Position = Hub,
            Radius = 11,
            Zone = MapZone.ProjectFiles,
            State = NodeState.Live,
            IsHub = true,
            ShowLabel = true,
        });

        PlaceZone(world, hubId, sources, MapZone.ProjectFiles, left: 200, top: 250, bottom: 1500);
        PlaceZone(world, hubId, sources, MapZone.ProjectMemory, left: 2050, top: 300, bottom: 1400);
        PlaceZone(world, hubId, sources, MapZone.Chats, left: 1400, top: 1250, bottom: 1550);

        return world;
    }

    /// <summary>
    /// "30 sources - 1 842 chunks". Real counts, and a spaced hyphen because
    /// ASCII punctuation is the rule.
    /// </summary>
    private static string BuildNote(int sources, int chunks)
    {
        var sourceText = sources == 1 ? "1 source" : $"{sources} sources";
        var chunkText = chunks == 1 ? "1 chunk" : $"{chunks:N0} chunks";
        return $"{sourceText} - {chunkText}";
    }

    private static void PlaceZone(
        MapWorld world,
        Guid hubId,
        IReadOnlyList<MapSource> sources,
        MapZone zone,
        float left,
        float top,
        float bottom)
    {
        var inZone = sources.Where(s => s.Zone == zone).ToList();
        if (inZone.Count == 0)
        {
            return;
        }

        var step = inZone.Count == 1 ? 0 : (bottom - top) / (inZone.Count - 1);

        for (var i = 0; i < inZone.Count; i++)
        {
            var source = inZone[i];
            var y = inZone.Count == 1 ? (top + bottom) / 2f : top + (step * i);

            var node = new MapNode
            {
                Id = Guid.NewGuid(),
                Label = source.Name,
                Position = new SKPoint(left, y),
                // Bigger sources read as bigger nodes, within the 7 to 18 range.
                Radius = Math.Clamp(7f + (source.ChunkCount * 0.15f), 7f, 18f),
                Zone = zone,
                State = NodeState.Idle,
                ShowLabel = true,
            };

            world.Nodes.Add(node);
            world.Edges.Add(new MapEdge { FromId = hubId, ToId = node.Id, Kind = EdgeKind.Idle });
        }
    }
}
