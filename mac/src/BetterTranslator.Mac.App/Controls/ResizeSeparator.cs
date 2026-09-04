using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace BetterTranslator.Mac.App.Controls;

public sealed class ResizeSeparator : Thumb
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ResizeSeparator, double>(
            nameof(Value),
            0d,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ResizeSeparator, double>(nameof(Minimum), 0d);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ResizeSeparator, double>(nameof(Maximum), double.PositiveInfinity);

    public static readonly StyledProperty<double> KeyboardStepProperty =
        AvaloniaProperty.Register<ResizeSeparator, double>(nameof(KeyboardStep), 8d);

    public static readonly StyledProperty<bool> InvertedProperty =
        AvaloniaProperty.Register<ResizeSeparator, bool>(nameof(Inverted), false);

    static ResizeSeparator() =>
        ValueProperty.Changed.AddClassHandler<ResizeSeparator>(OnValueChanged);

    public ResizeSeparator()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Focusable = true;
        DragDelta += OnDragDelta;
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double KeyboardStep
    {
        get => GetValue(KeyboardStepProperty);
        set => SetValue(KeyboardStepProperty, value);
    }

    public bool Inverted
    {
        get => GetValue(InvertedProperty);
        set => SetValue(InvertedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ResizeSeparator);

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

    private void OnDragDelta(object? sender, VectorEventArgs e) => Nudge(e.Vector.X);

    private void Nudge(double delta)
    {
        var change = Inverted ? -delta : delta;
        Value = Math.Clamp(Value + change, Minimum, Maximum);
    }

    private static void OnValueChanged(ResizeSeparator separator, AvaloniaPropertyChangedEventArgs e) =>
        AutomationProperties.SetItemStatus(
            separator,
            ((double)(e.NewValue ?? 0d)).ToString("0", CultureInfo.CurrentCulture) + " pixels");
}
