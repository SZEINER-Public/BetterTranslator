using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.ViewModels;

public enum ProjectScopeKind
{
    /// <summary>A repository root.</summary>
    Project,

    /// <summary>One folder from the project.</summary>
    Folder,

    /// <summary>Nothing chosen. The default, and translation still works.</summary>
    None,
}

/// <summary>
/// One row of the project selector's menu. The row that is currently in force
/// says so, which is how the menu shows the present setting without a tick. A
/// scope that cannot be chosen yet is locked rather than dropped, and says why
/// in both its tooltip and its automation name -- the same shape a locked
/// <see cref="Controls.SegmentItem"/> takes, because it is the same idea.
/// </summary>
public sealed partial class ProjectScopeViewModel(ProjectScopeKind kind) : ObservableObject
{
    public ProjectScopeKind Kind { get; } = kind;

    public string Title => Kind switch
    {
        ProjectScopeKind.Project => "Project",
        ProjectScopeKind.Folder => "Folder",
        _ => "None",
    };

    /// <summary>What this scope is, before the current-setting note.</summary>
    private string Description => Kind switch
    {
        ProjectScopeKind.Project => "Repository root. Adds preserved terms and past choices.",
        ProjectScopeKind.Folder => "One folder from the project.",
        _ => "Translation works without it.",
    };

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsLocked { get; set; }

    /// <summary>
    /// Why the row is locked. Shown as the tooltip and folded into the
    /// automation name, so a locked row never leaves the reason implicit.
    /// </summary>
    [ObservableProperty]
    public partial string? LockedReason { get; set; }

    public bool IsUnlocked => !IsLocked;

    public string AutomationName => IsLocked && !string.IsNullOrEmpty(LockedReason)
        ? $"{Title}. {LockedReason}"
        : Title;

    /// <summary>
    /// "Current setting." leads the line for whichever scope is in force, so
    /// the menu states the present setting rather than only offering changes.
    /// </summary>
    public string Detail => IsCurrent ? "Current setting. " + Description : Description;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(Detail));

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnLockedReasonChanged(string? value) => OnPropertyChanged(nameof(AutomationName));
}
