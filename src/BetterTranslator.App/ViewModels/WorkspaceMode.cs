namespace BetterTranslator.App.ViewModels;

/// <summary>
/// The three positions of the workspace mode slider. Memory is a place rather
/// than a translation setting: leaving it returns to the chat in whichever of
/// the two chat modes was chosen.
/// </summary>
public enum WorkspaceMode
{
    Memory,
    Simple,
    Advanced,
}
