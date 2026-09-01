using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Map.Camera;
using BetterTranslator.Map.Painting;
using SkiaSharp;

namespace BetterTranslator.Mac.App.Views;

public sealed class MapPaintEventArgs(SKCanvas canvas, SKSize size) : EventArgs
{
    public SKCanvas Canvas { get; } = canvas;

    public SKSize Size { get; } = size;
}

public sealed class MapSurface : Control
{
    private WriteableBitmap? _bitmap;

    public MapSurface() => SizeChanged += (_, _) => InvalidateVisual();

    public event EventHandler<MapPaintEventArgs>? PaintSurface;

    public float DeviceScale => (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d);

    public SKSize CanvasSize
    {
        get
        {
            var pixels = SurfacePixels();
            return new SKSize(pixels.Width, pixels.Height);
        }
    }

    public override void Render(DrawingContext context)
    {
        var pixels = SurfacePixels();

        if (pixels.Width <= 0 || pixels.Height <= 0 || PaintSurface is null)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize != pixels)
        {
            var scale = DeviceScale;

            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                pixels,
                new Vector(96d * scale, 96d * scale),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        using (var buffer = _bitmap.Lock())
        {
            var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, buffer.Address, buffer.RowBytes);

            if (surface is null)
            {
                return;
            }

            PaintSurface.Invoke(this, new MapPaintEventArgs(surface.Canvas, new SKSize(pixels.Width, pixels.Height)));
            surface.Flush();
        }

        context.DrawImage(
            _bitmap,
            new Rect(0, 0, pixels.Width, pixels.Height),
            new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    private PixelSize SurfacePixels()
    {
        var scale = DeviceScale;

        return new PixelSize(
            Math.Max(0, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(0, (int)Math.Ceiling(Bounds.Height * scale)));
    }
}

public partial class RetrievalMapView : UserControl
{
    private const float ArrowPan = 90f;

    private const float ShiftPan = 320f;

    private const float KeyZoomStep = 1.2f;

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly MapCamera _camera = new();

    private MapPainter? _painter;
    private RetrievalMapViewModel? _model;
    private Point? _panAnchor;

    public RetrievalMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Fit();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.FitRequested -= Fit;
            _model.RedrawRequested -= Redraw;
        }

        _model = DataContext as RetrievalMapViewModel;

        if (_model is not null)
        {
            _model.FitRequested += Fit;
            _model.RedrawRequested += Redraw;
        }
    }

    private void Redraw() => Canvas.InvalidateVisual();

    private void Fit()
    {
        if (_model is null || Canvas.Bounds.Width <= 0)
        {
            return;
        }

        _camera.DeviceScale = DeviceScale();
        _camera.Fit(CanvasSizeInDevicePixels(), _model.World.Bounds());

        Canvas.InvalidateVisual();
    }

    private float DeviceScale() => Canvas.Bounds.Width <= 0 ? 1f : Canvas.DeviceScale;

    private SKSize CanvasSizeInDevicePixels() => Canvas.CanvasSize;

    private void OnPaintSurface(object? sender, MapPaintEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        _camera.DeviceScale = DeviceScale();
        _painter ??= new MapPainter(BuildPalette());

        _painter.Paint(e.Canvas, _model.World, _camera, e.Size, dashPhase: 0f);
    }

    private static MapPalette BuildPalette() => new()
    {
        Surface = ToSkia("BrushSurface"),
        Text = ToSkia("BrushText"),
        TextTertiary = ToSkia("BrushTextTertiary"),
        NodeIdle = ToSkia("BrushNodeIdle"),
        AccentDeep = ToSkia("BrushAccentDeep"),
        Lexical = ToSkia("BrushRepoSource"),
        Success = ToSkia("BrushSuccess"),
        Warn = ToSkia("BrushWarn"),
        BorderSoft = ToSkia("BrushBorderSoft"),
    };

    private static SKColor ToSkia(string tokenKey)
    {
        var colour = Tokens.Get<System.Windows.Media.Brush>(tokenKey).Color;
        return new SKColor(colour.R, colour.G, colour.B, colour.A);
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Canvas.Focus();

        var properties = e.GetCurrentPoint(Canvas).Properties;

        if (properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && e.ClickCount == 2)
        {
            Fit();
            e.Handled = true;
            return;
        }

        if (properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            _panAnchor = e.GetPosition(Canvas);
            e.Pointer.Capture(Canvas);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle)
        {
            return;
        }

        _panAnchor = null;
        e.Pointer.Capture(null);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(Canvas);

        if (_panAnchor is { } anchor && e.GetCurrentPoint(Canvas).Properties.IsMiddleButtonPressed)
        {
            _camera.PanByDip((float)(position.X - anchor.X), (float)(position.Y - anchor.Y));
            _panAnchor = position;
            Canvas.InvalidateVisual();
            return;
        }

        if (_model is null)
        {
            return;
        }

        var world = _camera.ScreenToWorld(ToDevicePixels(position));
        var hit = _model.World.HitTest(world);

        _model.HoveredNode = hit;
        Canvas.Cursor = hit is null ? ArrowCursor : HandCursor;
    }

    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.15f : 1f / 1.15f;

        _camera.DeviceScale = DeviceScale();
        _camera.ZoomAt(ToDevicePixels(e.GetPosition(Canvas)), factor);

        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    private SKPoint ToDevicePixels(Point dip) =>
        new((float)(dip.X * _camera.DeviceScale), (float)(dip.Y * _camera.DeviceScale));

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
        var step = shift ? ShiftPan : ArrowPan;

        switch (e.Key)
        {
            case Key.Left:
                _camera.PanByWorld(-step, 0);
                break;

            case Key.Right:
                _camera.PanByWorld(step, 0);
                break;

            case Key.Up:
                _camera.PanByWorld(0, -step);
                break;

            case Key.Down:
                _camera.PanByWorld(0, step);
                break;

            case Key.OemPlus or Key.Add:
                _camera.ZoomAtCentre(CanvasSizeInDevicePixels(), KeyZoomStep);
                break;

            case Key.OemMinus or Key.Subtract:
                _camera.ZoomAtCentre(CanvasSizeInDevicePixels(), 1f / KeyZoomStep);
                break;

            case Key.D0 or Key.NumPad0:
                Fit();
                e.Handled = true;
                return;

            default:
                return;
        }

        Canvas.InvalidateVisual();
        e.Handled = true;
    }
}
