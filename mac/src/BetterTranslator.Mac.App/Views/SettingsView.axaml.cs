using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.Adapters;

namespace BetterTranslator.Mac.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _model;

    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += OnModelChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        Detach();

        _model = (DataContext as MacSettingsSurface)?.Settings;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;

            Resize(_model.IsConfigTab, animate: false);
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model = null;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsConfigTab) && _model is not null)
        {
            Resize(_model.IsConfigTab, animate: true);
        }
    }

    private void Resize(bool wide, bool animate)
    {
        var width = Tokens.Number(wide ? "SettingsConfigWidth" : "SettingsWindowWidth");
        var height = Tokens.Number(wide ? "SettingsConfigHeight" : "SettingsWindowHeight");

        if (!animate)
        {
            Surface.Transitions = null;
            Surface.Width = width;
            Surface.Height = height;
            return;
        }

        Surface.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = WidthProperty,
                Duration = Tokens.Get<TimeSpan>("Motion200"),
                Easing = Tokens.Get<Easing>("EaseStandard"),
            },
            new DoubleTransition
            {
                Property = HeightProperty,
                Duration = Tokens.Get<TimeSpan>("Motion200"),
                Easing = Tokens.Get<Easing>("EaseStandard"),
            },
        };

        Surface.Width = width;
        Surface.Height = height;
    }
}
