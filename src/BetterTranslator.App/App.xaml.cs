using System.Windows;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.App;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainWindowViewModel shell)
        {
            shell.AgentServer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
