using BetterTranslator.Map.Camera;
using BetterTranslator.Map.World;
using SkiaSharp;

namespace BetterTranslator.Map.Painting;

/// <summary>
/// Colours the painter needs, passed in from the token dictionary so the canvas
/// carries no palette of its own.
/// </summary>
public sealed record MapPalette
{
    public required SKColor Surface { get; init; }

    public required SKColor Text { get; init; }

    public required SKColor TextTertiary { get; init; }

    public required SKColor NodeIdle { get; init; }

    public required SKColor AccentDeep { get; init; }

    public required SKColor Lexical { get; init; }

    public required SKColor Success { get; init; }

    public required SKColor Warn { get; init; }

    public required SKColor BorderSoft { get; init; }
}

/// <summary>
/// One paint pass over the world. Everything is drawn here rather than as WPF
/// elements: a 2600 by 1700 world with pan, zoom and animated edges is not
/// viable as a visual tree.
/// </summary>
public sealed class MapPainter(MapPalette palette)
{
    /// <summary>Zone captions are 12 with a 1.1 per-glyph advance.</summary>
    private const float ZoneLabelSize = 12f;

    private const float ZoneLabelTracking = 1.1f;

    /// <summary>A label never renders below 11 on screen, at any zoom.</summary>
    private const float MinimumLabelSize = 11f;

    private const float EdgeStroke = 1.4f;

    public void Paint(SKCanvas canvas, MapWorld world, MapCamera camera, SKSize viewport, float dashPhase)
    {
        canvas.Clear(palette.Surface);

        if (world.IsEmpty)
        {
            return;
        }

        DrawZoneLabels(canvas, world, camera);
        DrawEdges(canvas, world, camera, dashPhase);
        DrawNodes(canvas, world, camera);
    }

    private void DrawZoneLabels(SKCanvas canvas, MapWorld world, MapCamera camera)
    {
        using var font = new SKFont(SKTypeface.Default, ZoneLabelSize * camera.DeviceScale);
        using var paint = new SKPaint { Color = palette.TextTertiary, IsAntialias = true };

        foreach (var (zone, label) in MapWorld.ZoneLabels)
        {
            var anchor = ZoneAnchor(world, zone);
            if (anchor is null)
            {
                continue;
            }

            var screen = camera.WorldToScreen(anchor.Value);
            DrawTracked(canvas, font, paint, label, screen, ZoneLabelTracking * camera.DeviceScale);
        }
    }

    /// <summary>
    /// Draws text one glyph at a time with an explicit advance. WPF has no
    /// tracking property and per-character Runs are not an acceptable stand-in,
    /// so the zone captions get their tracking here.
    /// </summary>
    private static void DrawTracked(
        SKCanvas canvas,
        SKFont font,
        SKPaint paint,
        string text,
        SKPoint origin,
        float tracking)
    {
        var x = origin.X;

        foreach (var glyph in text)
        {
            var s = glyph.ToString();
            canvas.DrawText(s, x, origin.Y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(s) + tracking;
        }
    }

    private static SKPoint? ZoneAnchor(MapWorld world, MapZone zone)
    {
        var inZone = world.Nodes.Where(n => n.Zone == zone && !n.IsHub).ToList();
        if (inZone.Count == 0)
        {
            return null;
        }

        var x = inZone.Min(n => n.Position.X);
        var y = inZone.Min(n => n.Position.Y) - 70;
        return new SKPoint(x, y);
    }

    private void DrawEdges(SKCanvas canvas, MapWorld world, MapCamera camera, float dashPhase)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = EdgeStroke * camera.DeviceScale,
        };

        foreach (var edge in world.Edges)
        {
            var from = world.FindById(edge.FromId);
            var to = world.FindById(edge.ToId);

            if (from is null || to is null)
            {
                continue;
            }

            var a = camera.WorldToScreen(from.Position);
            var b = camera.WorldToScreen(to.Position);

            paint.Color = ColourFor(edge.Kind);

            // A live retrieval animates a travelling dash along the curve.
            if (edge.IsLive)
            {
                var on = 6f * camera.DeviceScale;
                var off = 6f * camera.DeviceScale;
                paint.PathEffect = SKPathEffect.CreateDash([on, off], dashPhase * camera.DeviceScale);
            }
            else
            {
                paint.PathEffect = null;
            }

            // A gentle curve rather than a straight line, so edges leaving the
            // hub stay distinguishable where they bunch up.
            var midX = (a.X + b.X) / 2f;

            var builder = new SKPathBuilder();
            builder.MoveTo(a);
            builder.CubicTo(new SKPoint(midX, a.Y), new SKPoint(midX, b.Y), b);

            using var path = builder.Detach();
            canvas.DrawPath(path, paint);
            paint.PathEffect?.Dispose();
            paint.PathEffect = null;
        }
    }

    private SKColor ColourFor(EdgeKind kind) => kind switch
    {
        EdgeKind.Dense => palette.AccentDeep,
        EdgeKind.Lexical => palette.Lexical,
        EdgeKind.Fused => palette.Success,
        EdgeKind.Indexing => palette.Warn,
        _ => palette.BorderSoft,
    };

    private void DrawNodes(SKCanvas canvas, MapWorld world, MapCamera camera)
    {
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { IsAntialias = true, Color = palette.Text };
        using var notePaint = new SKPaint { IsAntialias = true, Color = palette.TextTertiary };

        foreach (var node in world.Nodes)
        {
            var centre = camera.WorldToScreen(node.Position);
            var radius = camera.WorldToScreenLength(node.Radius);

            var colour = node.State switch
            {
                NodeState.Live => palette.AccentDeep,
                NodeState.Indexing => palette.Warn,
                NodeState.Queued => palette.NodeIdle,
                _ => palette.NodeIdle,
            };

            if (node.IsHub)
            {
                fill.Color = palette.AccentDeep;
                canvas.DrawCircle(centre, radius, fill);
            }
            else
            {
                fill.Color = palette.Surface;
                canvas.DrawCircle(centre, radius, fill);

                ring.Color = colour;
                ring.StrokeWidth = 1.4f * camera.DeviceScale;
                canvas.DrawCircle(centre, radius, ring);
            }

            if (!node.ShowLabel)
            {
                continue;
            }

            // Labels do not shrink below the floor, however far out the camera is.
            var labelSize = Math.Max(MinimumLabelSize, 12f * camera.Zoom) * camera.DeviceScale;
            using var font = new SKFont(SKTypeface.Default, labelSize);

            var labelX = centre.X + radius + (8f * camera.DeviceScale);
            var labelY = centre.Y + (labelSize / 3f);

            canvas.DrawText(node.Label, labelX, labelY, SKTextAlign.Left, font, textPaint);

            if (node.Note is null)
            {
                continue;
            }

            var noteSize = Math.Max(MinimumLabelSize, 11f * camera.Zoom) * camera.DeviceScale;
            using var noteFont = new SKFont(SKTypeface.Default, noteSize);

            canvas.DrawText(
                node.Note,
                labelX,
                labelY + labelSize + (2f * camera.DeviceScale),
                SKTextAlign.Left,
                noteFont,
                notePaint);
        }
    }
}
