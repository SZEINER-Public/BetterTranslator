using SkiaSharp;

namespace BetterTranslator.Map.World;

public enum MapZone
{
    ProjectFiles,
    ProjectMemory,
    Chats,
}

public enum NodeState
{
    Idle,
    Queued,
    Indexing,
    Live,
}

public enum EdgeKind
{
    /// <summary>Dense vector retrieval.</summary>
    Dense,

    /// <summary>BM25 lexical retrieval, a distinct hue from dense.</summary>
    Lexical,

    /// <summary>Both agreed.</summary>
    Fused,

    Indexing,
    Idle,
}

/// <summary>
/// One node. The label is always a real name; no internal id ever reaches it.
/// </summary>
public sealed record MapNode
{
    public required Guid Id { get; init; }

    /// <summary>What the user sees. Never an id.</summary>
    public required string Label { get; init; }

    /// <summary>Second line under the hub, for example "30 sources - 1 842 chunks".</summary>
    public string? Note { get; init; }

    public required SKPoint Position { get; init; }

    /// <summary>Ring diameter in world units, 7 to 18 across.</summary>
    public required float Radius { get; init; }

    public required MapZone Zone { get; init; }

    public NodeState State { get; init; } = NodeState.Idle;

    /// <summary>True for the centre hub, which is named after the source.</summary>
    public bool IsHub { get; init; }

    /// <summary>Only the hub and the group nodes carry a visible label.</summary>
    public bool ShowLabel { get; init; }
}

public sealed record MapEdge
{
    public required Guid FromId { get; init; }

    public required Guid ToId { get; init; }

    public EdgeKind Kind { get; init; } = EdgeKind.Idle;

    /// <summary>True while a retrieval is running along this edge.</summary>
    public bool IsLive { get; init; }
}

/// <summary>
/// The graph the map paints. Built from what is actually indexed, so an empty
/// index produces an empty world rather than a placeholder graph.
/// </summary>
public sealed class MapWorld
{
    public List<MapNode> Nodes { get; } = [];

    public List<MapEdge> Edges { get; } = [];

    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>
    /// The zone captions, drawn on the canvas so their tracking can be applied
    /// per glyph. XAML has no tracking property, which is why they live here.
    /// </summary>
    public static IReadOnlyDictionary<MapZone, string> ZoneLabels { get; } =
        new Dictionary<MapZone, string>
        {
            [MapZone.ProjectFiles] = "PROJECT FILES",
            [MapZone.ProjectMemory] = "PROJECT MEMORY",
            [MapZone.Chats] = "CHATS",
        };

    public MapNode? FindById(Guid id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// Bounds of everything placed, for Fit. Falls back to the whole world when
    /// nothing is placed.
    /// </summary>
    public SKRect Bounds()
    {
        if (Nodes.Count == 0)
        {
            return new SKRect(0, 0, Camera.MapCamera.WorldSize.Width, Camera.MapCamera.WorldSize.Height);
        }

        var left = Nodes.Min(n => n.Position.X - n.Radius);
        var top = Nodes.Min(n => n.Position.Y - n.Radius);
        var right = Nodes.Max(n => n.Position.X + n.Radius);
        var bottom = Nodes.Max(n => n.Position.Y + n.Radius);

        // A little room so labels beside the outermost nodes are not clipped.
        return new SKRect(left - 120, top - 60, right + 120, bottom + 60);
    }

    /// <summary>
    /// Hit test in world space. The cursor changes only over a node, so this is
    /// what the view asks rather than walking a visual tree.
    /// </summary>
    public MapNode? HitTest(SKPoint world, float tolerance = 6f)
    {
        MapNode? best = null;
        var bestDistance = float.MaxValue;

        foreach (var node in Nodes)
        {
            var dx = world.X - node.Position.X;
            var dy = world.Y - node.Position.Y;
            var distance = MathF.Sqrt((dx * dx) + (dy * dy));

            if (distance <= node.Radius + tolerance && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }
}
