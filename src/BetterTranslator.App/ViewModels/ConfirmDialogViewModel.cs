using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// C13: dialogs are a layer inside MainWindow, never a second Window, so the
/// custom chrome stays visible behind them. A confirmation restates the
/// specific action rather than asking "are you sure".
/// </summary>
public sealed partial class ConfirmDialogViewModel(
    string title,
    string body,
    string confirmLabel,
    string cancelLabel,
    string confirmIconKey,
    string cancelIconKey,
    Action onConfirm,
    Action onClose) : ObservableObject
{
    public string Title { get; } = title;

    public string Body { get; } = body;

    public string ConfirmLabel { get; } = confirmLabel;

    public string CancelLabel { get; } = cancelLabel;

    /// <summary>
    /// Resource keys rather than icons, so the view model holds no WPF type and
    /// each dialog names its own glyph. There is no default: a confirmation is
    /// not always destructive, and a bin inherited by accident would say the
    /// wrong thing about an action that only saves or applies.
    /// </summary>
    public string ConfirmIconKey { get; } = confirmIconKey;

    public string CancelIconKey { get; } = cancelIconKey;

    [RelayCommand]
    private void Confirm()
    {
        onConfirm();
        onClose();
    }

    [RelayCommand]
    private void Cancel() => onClose();
}
