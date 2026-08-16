using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Map.Camera;
using BetterTranslator.Map.Painting;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace BetterTranslator.App.Views;

public partial class RetrievalMapView : UserControl
{
    /// <summary>Arrow keys pan 90, Shift plus arrow pans 320.</summary>
    private const float ArrowPan = 90f;

    private const float ShiftPan = 320f;

    private const float KeyZoomStep = 1.2f;

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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
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

    /// <summary>
    /// Frames the graph against the element's live size. Measured on every call
    /// rather than cached, so leaving the view and returning re-frames against
    /// the size it actually has now.
    /// </summary>
    private void Fit()
    {
        if (_model is null || Canvas.ActualWidth <= 0)
        {
            return;
        }

        _camera.DeviceScale = DeviceScale();
        _camera.Fit(CanvasSizeInDevicePixels(), _model.World.Bounds());

        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// CanvasSize is in device pixels, ActualWidth in device-independent ones,
    /// so the ratio is the scale the camera needs. Derived on every paint.
    /// </summary>
    private float DeviceScale() =>
        Canvas.ActualWidth <= 0 ? 1f : (float)(Canvas.CanvasSize.Width / Canvas.ActualWidth);

    private SKSize CanvasSizeInDevicePixels() => Canvas.CanvasSize;

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        _camera.DeviceScale = DeviceScale();
        _painter ??= new MapPainter(BuildPalette());

        _painter.Paint(e.Surface.Canvas, _model.World, _camera, e.Info.Size, dashPhase: 0f);
    }

    /// <summary>
    /// The canvas carries no palette of its own; every colour comes from the
    /// token dictionary.
    /// </summary>
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
        var colour = ((SolidColorBrush)Tokens.Get<Brush>(tokenKey)).Color;
        return new SKColor(colour.R, colour.G, colour.B, colour.A);
    }

    // ---- pointer ----

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        Canvas.Focus();

        // Double-click fits.
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            Fit();
            e.Handled = true;
            return;
        }

        // Middle drag pans. The left button never pans.
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panAnchor = e.GetPosition(Canvas);
            Canvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panAnchor = null;
        Canvas.ReleaseMouseCapture();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Canvas);

        if (_panAnchor is { } anchor && e.MiddleButton == MouseButtonState.Pressed)
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

        // The cursor changes only over a node, and the test runs against the
        // world model rather than a visual tree.
        var world = _camera.ScreenToWorld(ToDevicePixels(position));
        var hit = _model.World.HitTest(world);

        _model.HoveredNode = hit;
        Canvas.Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The wheel zooms at the cursor.
        var factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;

        _camera.DeviceScale = DeviceScale();
        _camera.ZoomAt(ToDevicePixels(e.GetPosition(Canvas)), factor);

        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    private SKPoint ToDevicePixels(Point dip) =>
        new((float)(dip.X * _camera.DeviceScale), (float)(dip.Y * _camera.DeviceScale));

    // ---- keyboard ----

    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
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
