using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

/// <summary>
/// A segmented control: one track, N fixed-width segments, and a white thumb
/// that slides and resizes between them. It is a ListBox so that selection,
/// keyboard traversal and the single-tab-stop convention come from the
/// framework; the thumb is the only thing this class adds.
/// </summary>
public sealed class SegmentedControl : ListBox
{
    private const string ThumbPart = "PART_Thumb";
    private const string TrackPart = "PART_Track";

    private FrameworkElement? _thumb;
    private FrameworkElement? _track;
    private TranslateTransform? _offset;

    static SegmentedControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SegmentedControl),
            new FrameworkPropertyMetadata(typeof(SegmentedControl)));
    }

    public SegmentedControl()
    {
        // Locked segments are non-selectable, so a click on one must not move
        // the thumb. IsEnabled false on the container already blocks it.
        SelectionMode = SelectionMode.Single;
        Loaded += (_, _) => PositionThumb(animate: false);
        SizeChanged += (_, _) => PositionThumb(animate: false);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _thumb = GetTemplateChild(ThumbPart) as FrameworkElement;
        _track = GetTemplateChild(TrackPart) as FrameworkElement;

        if (_thumb is not null)
        {
            // The transform is built here rather than in the template: WPF
            // freezes Freezables declared inside a ControlTemplate, and a
            // frozen transform cannot be animated.
            _offset = new TranslateTransform();
            _thumb.RenderTransform = _offset;
        }

        Dispatcher.BeginInvoke(() => PositionThumb(animate: false), DispatcherPriority.Loaded);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        PositionThumb(animate: true);
    }

    /// <summary>
    /// Moves the thumb onto the selected segment, animating both its offset and
    /// its width over 220 with EaseStandard. The two run together, so a segment
    /// of a different width does not snap.
    /// </summary>
    private void PositionThumb(bool animate)
    {
        if (_thumb is null || _track is null || _offset is null)
        {
            return;
        }

        if (SelectedItem is null ||
            ItemContainerGenerator.ContainerFromItem(SelectedItem) is not FrameworkElement container ||
            container.ActualWidth <= 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }

        _thumb.Visibility = Visibility.Visible;

        var left = container.TransformToAncestor(_track).Transform(default).X;
        var width = container.ActualWidth;

        if (!animate)
        {
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _thumb.BeginAnimation(WidthProperty, null);
            _offset.X = left;
            _thumb.Width = width;
            return;
        }

        var duration = (Duration)FindResource("Motion220");
        var easing = (KeySpline)FindResource("EaseStandard");

        MotionService.AnimateDouble(_offset, TranslateTransform.XProperty, left, duration, easing);
        MotionService.AnimateDouble(_thumb, WidthProperty, width, duration, easing);
    }
}
