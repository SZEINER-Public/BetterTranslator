using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BetterTranslator.Mac.Parity;

public sealed class TokenIndex
{
    private readonly Dictionary<uint, string> _byColor = [];

    private TokenIndex()
    {
    }

    public static TokenIndex Build(Application application)
    {
        var index = new TokenIndex();

        foreach (var key in Keys)
        {
            if (application.TryFindResource(key, out var value) && value is ISolidColorBrush brush)
            {
                index._byColor.TryAdd(brush.Color.ToUInt32(), key);
            }
        }

        return index;
    }

    public string? Resolve(IBrush? brush) =>
        brush is ISolidColorBrush solid && _byColor.TryGetValue(solid.Color.ToUInt32(), out var key)
            ? key
            : brush is null ? null : Unnamed(brush);

    private static string Unnamed(IBrush brush) =>
        brush is ISolidColorBrush solid ? "unnamed:" + solid.Color.ToString() : "unnamed:" + brush.GetType().Name;

    public static readonly string[] Keys =
    [
        "BrushDesk", "BrushWindow", "BrushSurface", "BrushSurfaceMuted", "BrushSurfaceSunken",
        "BrushTrack", "BrushTrackAlt", "BrushHover", "BrushHoverStrong", "BrushRowActive",
        "BrushSkeletonBar",
        "BrushText", "BrushTextSecondary", "BrushTextTertiary", "BrushTextMuted", "BrushTextSubtle", "BrushIcon",
        "BrushBorder", "BrushBorderSoft", "BrushBorderFaint", "BrushBorderHairline", "BrushBorderStrong", "BrushWindowEdge",
        "BrushAccent", "BrushAccentDeep", "BrushAccentPressed", "BrushAccentTint", "BrushAccentTintSoft",
        "BrushAccentBorder", "BrushAccentHover", "BrushAccentBorderStrong", "BrushSelection",
        "BrushSuccess", "BrushSuccessDeep", "BrushWarn", "BrushWarnSurface", "BrushWarnBorder", "BrushWarnText",
        "BrushDanger", "BrushDangerSurface", "BrushDangerBorder", "BrushDangerText",
        "BrushChatSource", "BrushRepoSource", "BrushNodeIdle", "BrushProgressTrack",
        "BrushOverlay", "BrushBadgeOverlay",
    ];
}
