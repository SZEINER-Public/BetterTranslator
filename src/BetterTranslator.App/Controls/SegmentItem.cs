using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.Controls;

/// <summary>
/// One segment of a <see cref="SegmentedControl"/>. A segment whose material
/// does not exist is locked rather than hidden, and says why in both its
/// tooltip and its automation name.
/// </summary>
public sealed partial class SegmentItem : ObservableObject
{
    /// <summary>The value this segment selects, for example a WorkspaceMode.</summary>
    public required object Value { get; init; }

    public required string Label { get; init; }

    /// <summary>Fixed segment width. The thumb animates to match it.</summary>
    public required double Width { get; init; }

    /// <summary>Optional leading glyph, drawn from an icon token.</summary>
    public IconDefinition? Icon { get; init; }

    /// <summary>
    /// Foreground for this segment while it is the selected one. Memory is the
    /// only segment that overrides it, taking BrushAccentDeep.
    /// </summary>
    public Brush? ActiveForeground { get; init; }

    [ObservableProperty]
    public partial bool IsLocked { get; set; }

    /// <summary>
    /// Why the segment is locked. Shown as the tooltip and folded into the
    /// automation name, so a locked control never leaves the reason implicit.
    /// </summary>
    [ObservableProperty]
    public partial string? LockedReason { get; set; }

    public string AutomationName => IsLocked && !string.IsNullOrEmpty(LockedReason)
        ? $"{Label}. {LockedReason}"
        : Label;

    public bool IsUnlocked => !IsLocked;

    public bool HasIcon => Icon is not null;

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(IsUnlocked));
    }

    partial void OnLockedReasonChanged(string? value) => OnPropertyChanged(nameof(AutomationName));
}
