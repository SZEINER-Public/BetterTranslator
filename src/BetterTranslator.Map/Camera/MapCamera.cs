using SkiaSharp;

namespace BetterTranslator.Map.Camera;

/// <summary>
/// Maps the 2600 by 1700 world onto the canvas.
///
/// SKElement reports CanvasSize in device pixels while ActualWidth is in
/// device-independent pixels, so the camera carries the ratio between them and
/// works in device pixels throughout. Mouse input arrives in DIPs and is
/// converted on the way in; hit testing then runs against the world model
/// rather than the visual tree.
/// </summary>
public sealed class MapCamera
{
    /// <summary>The world every map is laid out in.</summary>
    public static readonly SKSize WorldSize = new(2600, 1700);

    private const float MinZoom = 0.15f;
    private const float MaxZoom = 6f;

    /// <summary>World units per device-independent pixel.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>World point at the canvas origin.</summary>
    public SKPoint Offset { get; private set; }

    /// <summary>Device pixels per device-independent pixel.</summary>
    public float DeviceScale { get; set; } = 1f;

    /// <summary>Combined world-to-device-pixel factor.</summary>
    public float Scale => Zoom * DeviceScale;

    public SKPoint WorldToScreen(SKPoint world) => new(
        (world.X - Offset.X) * Scale,
        (world.Y - Offset.Y) * Scale);

    public SKPoint ScreenToWorld(SKPoint screen) => new(
        (screen.X / Scale) + Offset.X,
        (screen.Y / Scale) + Offset.Y);

    /// <summary>Converts a length from world units to device pixels.</summary>
    public float WorldToScreenLength(float length) => length * Scale;

    /// <summary>
    /// Pans by a drag measured in device-independent pixels, which is what a
    /// mouse delta arrives as.
    /// </summary>
    public void PanByDip(float dxDip, float dyDip) =>
        Offset = new SKPoint(Offset.X - (dxDip / Zoom), Offset.Y - (dyDip / Zoom));

    /// <summary>Pans by a fixed world distance, used by the arrow keys.</summary>
    public void PanByWorld(float dx, float dy) => Offset = new SKPoint(Offset.X + dx, Offset.Y + dy);

    /// <summary>
    /// Zooms about a device-pixel anchor, so the world point under the cursor
    /// stays under the cursor.
    /// </summary>
    public void ZoomAt(SKPoint screenAnchor, float factor)
    {
        var before = ScreenToWorld(screenAnchor);

        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

        var after = ScreenToWorld(screenAnchor);

        Offset = new SKPoint(
            Offset.X + (before.X - after.X),
            Offset.Y + (before.Y - after.Y));
    }

    /// <summary>Zooms about the middle of the viewport, used by plus and minus.</summary>
    public void ZoomAtCentre(SKSize viewportDevicePx, float factor) =>
        ZoomAt(new SKPoint(viewportDevicePx.Width / 2f, viewportDevicePx.Height / 2f), factor);

    /// <summary>
    /// Frames the given world bounds in the viewport. The viewport is passed in
    /// on every call rather than cached, so leaving the view and returning to it
    /// re-frames against the size the element actually has now.
    /// </summary>
    public void Fit(SKSize viewportDevicePx, SKRect worldBounds, float paddingDip = 40f)
    {
        if (viewportDevicePx.Width <= 0 || viewportDevicePx.Height <= 0 ||
            worldBounds.Width <= 0 || worldBounds.Height <= 0)
        {
            return;
        }

        var padding = paddingDip * DeviceScale * 2f;
        var usableWidth = Math.Max(1f, viewportDevicePx.Width - padding);
        var usableHeight = Math.Max(1f, viewportDevicePx.Height - padding);

        var zoom = Math.Min(usableWidth / worldBounds.Width, usableHeight / worldBounds.Height) / DeviceScale;
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        // Centre what was framed.
        var viewWorldWidth = viewportDevicePx.Width / Scale;
        var viewWorldHeight = viewportDevicePx.Height / Scale;

        Offset = new SKPoint(
            worldBounds.MidX - (viewWorldWidth / 2f),
            worldBounds.MidY - (viewWorldHeight / 2f));
    }

    /// <summary>Resets to a known state, used when the map is first shown.</summary>
    public void Reset()
    {
        Zoom = 1f;
        Offset = SKPoint.Empty;
    }
}
