using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.Mac.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainWindowViewModel? Shell => DataContext as MainWindowViewModel;

    private static void Run(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void OnMenuNewChat(object? sender, EventArgs e) => Run(Shell?.Workspace.NewChatCommand);

    private void OnMenuAddSources(object? sender, EventArgs e) => Run(Shell?.OpenAddSourcesCommand);

    private void OnMenuSettings(object? sender, EventArgs e) => Run(Shell?.OpenSettingsCommand);

    private void OnMenuDownloads(object? sender, EventArgs e) => Run(Shell?.OpenDownloadsCommand);

    private void OnMenuCloseWindow(object? sender, EventArgs e) => Close();

    private void OnMenuToggleSidebar(object? sender, EventArgs e) => Run(Shell?.ToggleSidebarCommand);

    private void OnMenuToggleRightPanel(object? sender, EventArgs e) => Run(Shell?.ToggleRightPanelCommand);

    private void OnMenuToggleRuntimePanel(object? sender, EventArgs e) => Run(Shell?.Runtime.TogglePanelCommand);

    private void OnMenuSimpleMode(object? sender, EventArgs e) => SelectMode(0);

    private void OnMenuAdvancedMode(object? sender, EventArgs e) => SelectMode(1);

    private void OnMenuBackToChat(object? sender, EventArgs e) => Run(Shell?.BackToChatCommand);

    private void OnMenuStartRuntime(object? sender, EventArgs e) => Run(Shell?.Runtime.StartCommand);

    private void OnMenuPauseRuntime(object? sender, EventArgs e) => Run(Shell?.Runtime.PauseCommand);

    private void OnMenuUnloadRuntime(object? sender, EventArgs e) => Run(Shell?.Runtime.UnloadCommand);

    private void SelectMode(int index)
    {
        if (Shell is { } shell && index < shell.WorkspaceModes.Count)
        {
            shell.SelectedWorkspaceMode = shell.WorkspaceModes[index];
        }
    }

    private void OnBackdropClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: ICommand command } && ReferenceEquals(e.Source, sender))
        {
            Run(command);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Shell is not { } shell)
        {
            return;
        }

        var closers = new ICommand?[]
        {
            shell.ActiveDialog?.CancelCommand,
            shell.AddSources?.CancelCommand,
            shell.Settings?.CloseCommand,
            shell.FirstRun?.DismissCommand,
        };

        foreach (var command in closers)
        {
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                e.Handled = true;
                return;
            }
        }
    }
}
