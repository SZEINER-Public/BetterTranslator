using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.Platform;

namespace BetterTranslator.Mac.App;

public partial class App : Application
{
    private MacPlatformSeams? _seams;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new MainWindow();

        _seams = new MacPlatformSeams(() => window);
        _seams.Install();

        if (!_seams.SingleInstance.TryClaim(out var reason))
        {
            _ = _seams.SingleInstance.SignalExistingAsync(desktop.Args ?? []);
            desktop.Shutdown(0);

            Console.Error.WriteLine(reason);
            return;
        }

        var shell = new MainWindowViewModel
        {
            ExitProcess = code => Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown(code)),
        };

        shell.ReleaseSingleInstance = _ =>
        {
            _seams?.Dispose();
            _seams = null;

            return Task.CompletedTask;
        };

        window.DataContext = shell;

        System.Windows.Application.Current!.MainWindow = new MainWindowSurface(window);
        System.Windows.Application.Current!.ShutdownHandler = code => desktop.Shutdown(code);

        desktop.MainWindow = window;
        desktop.Exit += (_, _) => _seams?.Dispose();
    }
}
