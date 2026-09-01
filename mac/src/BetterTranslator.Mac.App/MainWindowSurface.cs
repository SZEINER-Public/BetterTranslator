using Avalonia.Controls;
using SW = System.Windows;

namespace BetterTranslator.Mac.App;

public sealed class MainWindowSurface(Window window) : SW.IMainWindowSurface
{
    public object? DataContext => window.DataContext;

    public SW.WindowState WindowState
    {
        get => window.WindowState switch
        {
            Avalonia.Controls.WindowState.Minimized => SW.WindowState.Minimized,
            Avalonia.Controls.WindowState.Maximized => SW.WindowState.Maximized,
            Avalonia.Controls.WindowState.FullScreen => SW.WindowState.FullScreen,
            _ => SW.WindowState.Normal,
        };

        set => window.WindowState = value switch
        {
            SW.WindowState.Minimized => Avalonia.Controls.WindowState.Minimized,
            SW.WindowState.Maximized => Avalonia.Controls.WindowState.Maximized,
            SW.WindowState.FullScreen => Avalonia.Controls.WindowState.FullScreen,
            _ => Avalonia.Controls.WindowState.Normal,
        };
    }

    public void Activate() => window.Activate();
}
