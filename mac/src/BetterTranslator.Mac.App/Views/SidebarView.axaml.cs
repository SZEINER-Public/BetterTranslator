using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using BetterTranslator.App.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.Mac.App.Views;

public partial class SidebarView : UserControl
{
    private ChatWorkspaceViewModel? _workspace;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
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

    private void RunSweep(bool open)
    {
        var target = open ? SweepHost.Bounds.Width : 0d;

        Glide(SearchSweep, WidthProperty, target, Tokens.Get<TimeSpan>("Motion300"));
        Glide(SearchSweep, OpacityProperty, open ? 1d : 0d, Tokens.Get<TimeSpan>("Motion140"));

        SearchToggle.Icon = Tokens.Get<IconDefinition>(open ? "IconCloseCrossSearch" : "IconSearch");
        ToolTip.SetTip(SearchToggle, open ? "Close search" : "Search chats");
        AutomationProperties.SetName(SearchToggle, open ? "Close search" : "Search chats");

        if (open)
        {
            SearchField.Focus();
        }
    }

    private static void Glide(Animatable element, StyledProperty<double> property, double to, TimeSpan duration)
    {
        var from = element.GetValue(property);

        var animation = new Animation
        {
            Duration = duration,
            Easing = Tokens.Get<Easing>("EaseStandard"),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(property, double.IsNaN(from) ? 0d : from) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(property, to) },
                },
            },
        };

        _ = animation.RunAsync(element);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _workspace?.CloseSearchCommand.Execute(null);
        e.Handled = true;
    }
}
