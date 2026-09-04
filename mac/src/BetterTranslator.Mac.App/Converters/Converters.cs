namespace BetterTranslator.Mac.App.Converters;

public static class Converters
{
    public static readonly EqualToVisibilityConverter EqualToVisibility = new();

    public static readonly InverseBooleanToVisibilityConverter InverseBoolToVisibility = new();

    public static readonly StringEmptyToVisibilityConverter StringEmptyToVisibility = new();

    public static readonly StringNotEmptyToVisibilityConverter NotEmptyToVisibility = new();

    public static readonly ZeroToVisibilityConverter ZeroToVisibility = new();

    public static readonly ResourceKeyToBrushConverter FlagBrush = new();

    public static readonly ResourceKeyToIconConverter KeyToIcon = new();

    public static readonly BrushShimConverter BrushShim = new();
}
