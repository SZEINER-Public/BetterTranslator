using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public sealed class SegmentedControl : ListBox
{
    private const string ThumbPart = "PART_Thumb";
    private const string TrackPart = "PART_Track";

    private Control? _thumb;
    private Control? _track;
    private TranslateTransform? _offset;

    public SegmentedControl()
    {
        SelectionMode = SelectionMode.Single;
        Loaded += (_, _) => PositionThumb(animate: false);
        SizeChanged += (_, _) => PositionThumb(animate: false);
        SelectionChanged += (_, _) => PositionThumb(animate: true);
    }

    protected override Type StyleKeyOverride => typeof(SegmentedControl);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _thumb = e.NameScope.Find<Control>(ThumbPart);
        _track = e.NameScope.Find<Control>(TrackPart);

        if (_thumb is not null)
        {
            _offset = new TranslateTransform();
            _thumb.RenderTransform = _offset;
        }

        Dispatcher.UIThread.Post(() => PositionThumb(animate: false), DispatcherPriority.Loaded);
    }

    private void PositionThumb(bool animate)
    {
        if (_thumb is null || _track is null || _offset is null)
        {
            return;
        }

        if (SelectedItem is null ||
            ContainerFromItem(SelectedItem) is not { } container ||
            container.Bounds.Width <= 0 ||
            container.TranslatePoint(default, _track) is not { } origin)
        {
            _thumb.IsVisible = false;
            return;
        }

        _thumb.IsVisible = true;

        var left = origin.X;
        var width = container.Bounds.Width;

        if (!animate)
        {
            _offset.Transitions = null;
            _thumb.Transitions = null;
            _offset.X = left;
            _thumb.Width = width;
            return;
        }

        var duration = Tokens.Get<TimeSpan>("Motion220");
        var easing = Tokens.Get<Easing>("EaseStandard");

        _offset.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = duration,
                Easing = easing,
            },
        };

        _thumb.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = WidthProperty,
                Duration = duration,
                Easing = easing,
            },
        };

        _offset.X = left;
        _thumb.Width = width;
    }
}
