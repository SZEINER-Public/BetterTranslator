using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BetterTranslator.App.Controls;

/// <summary>
/// The draggable rule between two panes. It carries a grip at its midpoint that
/// is drawn at rest, and it is a real focusable control with arrow-key resizing,
/// because a drag with no single-pointer or keyboard alternative locks out
/// anyone who cannot drag.
/// </summary>
public sealed class ResizeSeparator : Thumb
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(ResizeSeparator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ResizeSeparator), new PropertyMetadata(0d));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ResizeSeparator), new PropertyMetadata(double.PositiveInfinity));

    public static readonly DependencyProperty KeyboardStepProperty = DependencyProperty.Register(
        nameof(KeyboardStep), typeof(double), typeof(ResizeSeparator), new PropertyMetadata(8d));

    /// <summary>
    /// Set false when dragging right should shrink the pane rather than grow
    /// it, which is the case for a pane docked to the right edge.
    /// </summary>
    public static readonly DependencyProperty InvertedProperty = DependencyProperty.Register(
        nameof(Inverted), typeof(bool), typeof(ResizeSeparator), new PropertyMetadata(false));

    static ResizeSeparator()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ResizeSeparator),
            new FrameworkPropertyMetadata(typeof(ResizeSeparator)));
    }

    public ResizeSeparator()
    {
        Cursor = Cursors.SizeWE;
        Focusable = true;
        DragDelta += OnDragDelta;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double KeyboardStep
    {
        get => (double)GetValue(KeyboardStepProperty);
        set => SetValue(KeyboardStepProperty, value);
    }

    public bool Inverted
    {
        get => (bool)GetValue(InvertedProperty);
        set => SetValue(InvertedProperty, value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left => -KeyboardStep,
            Key.Right => KeyboardStep,
            Key.Home => Minimum - Value,
            Key.End => Maximum - Value,
            _ => 0d,
        };

        if (step == 0d)
        {
            base.OnKeyDown(e);
            return;
        }

        Nudge(step);
        e.Handled = true;
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e) => Nudge(e.HorizontalChange);

    private void Nudge(double delta)
    {
        var change = Inverted ? -delta : delta;
        Value = Math.Clamp(Value + change, Minimum, Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Screen readers get the live figure, not just the control's name.
        var separator = (ResizeSeparator)d;
        AutomationProperties.SetItemStatus(
            separator,
            ((double)e.NewValue).ToString("0", CultureInfo.CurrentCulture) + " pixels");
    }
}
