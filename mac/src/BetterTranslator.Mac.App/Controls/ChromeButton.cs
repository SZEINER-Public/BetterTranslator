using Avalonia;
using Avalonia.Controls;
using BetterTranslator.App.Controls;

namespace BetterTranslator.Mac.App.Controls;

public class ChromeButton : Button
{
    public static readonly StyledProperty<IconDefinition?> IconProperty =
        AvaloniaProperty.Register<ChromeButton, IconDefinition?>(nameof(Icon));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ChromeButton, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsOutlinedProperty =
        AvaloniaProperty.Register<ChromeButton, bool>(nameof(IsOutlined));

    public IconDefinition? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsOutlined
    {
        get => GetValue(IsOutlinedProperty);
        set => SetValue(IsOutlinedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ChromeButton);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty)
        {
            PseudoClasses.Set(":active", change.GetNewValue<bool>());
        }
        else if (change.Property == IsOutlinedProperty)
        {
            PseudoClasses.Set(":outlined", change.GetNewValue<bool>());
        }
    }
}
