using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _model;

    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += OnModelChanged;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// The Config tab edits JSON and needs the room; the other four are short
    /// lists that would sit in space at that size. So the surface grows for one
    /// tab and shrinks back for the rest.
    ///
    /// Driven from here rather than from a XAML trigger because every animation
    /// in this application goes through MotionService, which is the single place
    /// the reduced-motion flag is read. A Storyboard in the view would bypass it
    /// and animate for someone who has asked the system not to.
    /// </summary>
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        _model = DataContext as SettingsViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;

            // No animation on the way in: the panel has just appeared, and
            // growing it immediately after would read as the window settling.
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
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            Surface.Width = width;
            Surface.Height = height;
            return;
        }

        var duration = Tokens.Get<Duration>("Motion200");
        var easing = Tokens.Get<KeySpline>("EaseStandard");

        MotionService.AnimateDouble(Surface, WidthProperty, width, duration, easing);
        MotionService.AnimateDouble(Surface, HeightProperty, height, duration, easing);
    }
}
