using System.Windows;
using System.Windows.Controls;

namespace BetterTranslator.App.Controls;

/// <summary>
/// C1: the square icon button used all over the chrome. It carries its icon as
/// an <see cref="IconDefinition"/>, which the template renders in the button's
/// own foreground, so hover, pressed and disabled states recolour the glyph
/// without the caller wiring anything up.
/// </summary>
public class ChromeButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconDefinition), typeof(ChromeButton), new PropertyMetadata(null));

    /// <summary>
    /// True while the thing this button toggles is showing. An active button
    /// takes the accent tint, which is how the sidebar toggle reads as on.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ChromeButton), new PropertyMetadata(false));

    /// <summary>
    /// True for the toolbar buttons, which sit on a surface of their own with
    /// a visible outline. The border is always 1 pixel and merely changes
    /// colour, so taking or losing the outline shifts nothing around it.
    /// </summary>
    public static readonly DependencyProperty IsOutlinedProperty = DependencyProperty.Register(
        nameof(IsOutlined), typeof(bool), typeof(ChromeButton), new PropertyMetadata(false));

    static ChromeButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ChromeButton),
            new FrameworkPropertyMetadata(typeof(ChromeButton)));
    }

    public IconDefinition? Icon
    {
        get => (IconDefinition?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsOutlined
    {
        get => (bool)GetValue(IsOutlinedProperty);
        set => SetValue(IsOutlinedProperty, value);
    }
}
