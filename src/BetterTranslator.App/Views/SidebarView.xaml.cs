using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.App.Views;

public partial class SidebarView : UserControl
{
    private ChatWorkspaceViewModel? _workspace;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        _workspace = DataContext as ChatWorkspaceViewModel;

        if (_workspace is not null)
        {
            _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatWorkspaceViewModel.IsSearchOpen))
        {
            RunSweep(_workspace!.IsSearchOpen);
        }
    }

    /// <summary>
    /// The search field wipes across the New chat button: a clipping container
    /// whose width animates over 300 with EaseStandard, with the field fading
    /// over the shorter 140 underneath it. The wipe alone lands hard at both
    /// ends; the fade is what carries it. The magnifier crosses over to a close
    /// cross at the same time.
    /// </summary>
    private void RunSweep(bool open)
    {
        var target = open ? SweepHost.ActualWidth : 0;

        MotionService.AnimateDouble(
            SearchSweep,
            WidthProperty,
            target,
            Tokens.Get<Duration>("Motion300"),
            Tokens.Get<KeySpline>("EaseStandard"));

        MotionService.AnimateDouble(
            SearchSweep,
            OpacityProperty,
            open ? 1 : 0,
            Tokens.Get<Duration>("Motion140"),
            Tokens.Get<KeySpline>("EaseStandard"));

        SearchToggle.Icon = Tokens.Get<Controls.IconDefinition>(open ? "IconCloseCrossSearch" : "IconSearch");
        SearchToggle.ToolTip = open ? "Close search" : "Search chats";
        System.Windows.Automation.AutomationProperties.SetName(
            SearchToggle,
            open ? "Close search" : "Search chats");

        if (open)
        {
            SearchField.Focus();
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // Escape closes and clears.
        _workspace?.CloseSearchCommand.Execute(null);
        e.Handled = true;
    }
}
