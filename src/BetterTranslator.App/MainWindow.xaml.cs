using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// The chevron's own transform. Built here rather than in markup so it is
    /// this element's alone and is never handed over frozen.
    /// </summary>
    private readonly RotateTransform _chevronRotation = new();

    public MainWindow()
    {
        InitializeComponent();

        StateChanged += (_, _) => SyncMaximizeAffordance();
        SyncMaximizeAffordance();

        SizeChanged += OnSizeChanged;
        MouseEnter += (_, _) => FadeResizeGrip(1);
        MouseLeave += (_, _) => FadeResizeGrip(0);

        ScopeChevron.RenderTransform = _chevronRotation;

        // handledEventsToo, so a click a control has already dealt with still
        // dismisses the menu the way a click on bare chrome does.
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDownAnywhere), handledEventsToo: true);
        Deactivated += (_, _) => CloseProjectMenu();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded can come round more than once; subscribing twice would run
        // every animation twice.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncSidebar(animate: false);
        SyncRightPanel(animate: false);
        SyncPreview(animate: false);

        // The preview belongs to the workspace, so its open state is watched
        // there rather than on the window.
        ViewModel.Workspace.Preview.PropertyChanged -= OnPreviewPropertyChanged;
        ViewModel.Workspace.Preview.PropertyChanged += OnPreviewPropertyChanged;

        await ViewModel.InitializeAsync(CancellationToken.None);
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePreviewViewModel.IsOpen))
        {
            SyncPreview(animate: true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsProjectMenuOpen):
                TurnChevron(ViewModel.IsProjectMenuOpen);
                break;

            case nameof(MainWindowViewModel.IsSidebarVisible):
                SyncSidebar(animate: true);
                break;

            case nameof(MainWindowViewModel.IsRightPanelVisible):
                SyncRightPanel(animate: true);
                break;

            // Entering or leaving Memory opens and closes the chat list with it.
            case nameof(MainWindowViewModel.IsMemoryMode):
                SyncSidebar(animate: true);
                break;

            // A separator writes the width the view model holds, and the host
            // has to follow it, since it no longer binds to it.
            case nameof(MainWindowViewModel.SidebarWidth) when ViewModel.IsSidebarVisible:
                SettlePanel(SidebarHost, ViewModel.SidebarWidth);
                break;

            case nameof(MainWindowViewModel.RightPanelWidth) when ViewModel.IsRightPanelVisible:
                SettlePanel(RightPanelHost, ViewModel.RightPanelWidth);
                break;

            case nameof(MainWindowViewModel.PreviewWidth) when ViewModel.Workspace.Preview.IsOpen:
                SettlePanel(PreviewHost, ViewModel.PreviewWidth);
                break;
        }
    }

    /// <summary>
    /// Points the chevron up while the menu is showing. 180 rather than -180 so
    /// the return trip retraces the same arc instead of spinning on round.
    /// </summary>
    private void TurnChevron(bool open) =>
        MotionService.AnimateDouble(
            _chevronRotation,
            RotateTransform.AngleProperty,
            open ? 180 : 0,
            Tokens.Get<Duration>("Motion170"),
            Tokens.Get<KeySpline>("EaseStandard"));

    /// <summary>
    /// Dismisses the scope menu on a click that is not the selector's own. The
    /// selector is skipped because its own command does the closing: handling
    /// it here as well would close and reopen in one click.
    /// </summary>
    private void OnPreviewMouseDownAnywhere(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsProjectMenuOpen || IsWithin(e.OriginalSource as DependencyObject, ProjectSelector))
        {
            return;
        }

        CloseProjectMenu();
    }

    /// <summary>
    /// Closes a modal layer when its dim area is clicked, and only then. An
    /// InputBinding on the layer fired for clicks anywhere inside it, the
    /// dialog included, so a miss beside a checkbox shut the window being
    /// filled in. The command to run rides on the layer's Tag.
    /// </summary>
    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) &&
            sender is FrameworkElement { Tag: ICommand close } &&
            close.CanExecute(null))
        {
            close.Execute(null);
        }
    }

    private void CloseProjectMenu()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsProjectMenuOpen = false;
        }
    }

    /// <summary>
    /// Walks up from the clicked element. The visual parent runs out at a
    /// template boundary, so the logical parent carries on from there.
    /// </summary>
    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// Memory takes the whole window. Its own rail of sources and learned terms
    /// sits where the chat list would, and two rails side by side would leave
    /// the reader working out which list they were looking at.
    /// </summary>
    private void SyncSidebar(bool animate) =>
        SyncPanel(
            SidebarHost,
            SidebarSeparator,
            ViewModel.IsSidebarVisible && !ViewModel.IsMemoryMode,
            ViewModel.SidebarWidth,
            animate);

    private void SyncPreview(bool animate) =>
        SyncPanel(
            PreviewHost,
            PreviewSeparator,
            ViewModel.Workspace.Preview.IsOpen,
            ViewModel.PreviewWidth,
            animate);

    private void SyncRightPanel(bool animate) =>
        SyncPanel(RightPanelHost, RightPanelSeparator, ViewModel.IsRightPanelVisible, ViewModel.RightPanelWidth, animate);

    /// <summary>
    /// Opens and closes a side panel. The host's width carries the motion while
    /// the panel inside holds its own, so the content is clipped on the way out
    /// rather than reflowing narrower and narrower. On the way back in the
    /// animation is cleared, handing width to its binding so the separator can
    /// go on resizing it.
    ///
    /// One method rather than one per panel: the two differ only in which
    /// elements they move, and a second copy would be the place the next fix
    /// gets applied to only one of them.
    /// </summary>
    private static void SyncPanel(Border host, UIElement separator, bool open, double width, bool animate)
    {
        if (!animate)
        {
            SettlePanel(host, open ? width : 0);
            host.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            separator.Visibility = host.Visibility;
            return;
        }

        var duration = Tokens.Get<Duration>("Motion200");
        var easing = Tokens.Get<KeySpline>("EaseStandard");

        if (open)
        {
            host.Visibility = Visibility.Visible;
            separator.Visibility = Visibility.Visible;

            MotionService.AnimateDouble(
                host, WidthProperty, width, duration, easing,
                () => SettlePanel(host, width));
            return;
        }

        // The separator stays up for the whole close, so the divider rides the
        // panel edge inwards rather than blinking out ahead of it.
        MotionService.AnimateDouble(
            host, WidthProperty, 0, duration, easing,
            () =>
            {
                SettlePanel(host, 0);
                // Collapsed outright, so a shut panel cannot be tabbed into.
                host.Visibility = Visibility.Collapsed;
                separator.Visibility = Visibility.Collapsed;
            });
    }

    /// <summary>
    /// Drops the animation and writes the width plainly. An animation left
    /// holding its end value goes on overriding the property, which would leave
    /// the separator unable to resize the panel it had just opened.
    /// </summary>
    private static void SettlePanel(Border host, double width)
    {
        host.BeginAnimation(WidthProperty, null);
        host.Width = width;
    }

    /// <summary>Escape closes the topmost layer.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel.IsProjectMenuOpen)
        {
            CloseProjectMenu();
            e.Handled = true;
            return;
        }

        // A confirmation is modal, so it answers the keyboard before anything
        // behind it does: Escape cancels, Enter takes the action it names. Both
        // go through the dialog's own commands rather than closing the layer, so
        // whatever a dialog does on the way out still runs.
        if (ViewModel.ActiveDialog is { } dialog && e.Key is Key.Escape or Key.Enter)
        {
            var command = e.Key == Key.Escape ? dialog.CancelCommand : dialog.ConfirmCommand;
            command.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.HasSettings)
        {
            ViewModel.Settings!.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.HasAddSources)
        {
            ViewModel.AddSources!.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// The size pill is driven from SizeChanged and hidden by the view model
    /// 400 after the last change, so it is up only while a drag is running.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Before the early return: the inset is measured off the window rect,
        // and this is the point at which that rect is the maximized one. Also
        // the only notice given when a maximized window is dragged to a monitor
        // with a different scale factor or a taskbar on another edge.
        SyncWindowInset();

        if (WindowState != WindowState.Normal)
        {
            return;
        }

        ViewModel.ReportWindowSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void FadeResizeGrip(double to) =>
        MotionService.AnimateDouble(
            ResizeGrip,
            OpacityProperty,
            to,
            Tokens.Get<Duration>("Motion160"),
            Tokens.Get<KeySpline>("EaseStandard"));

    /// <summary>
    /// The caption glyph and its labels follow the state, so a maximized
    /// window offers Restore rather than claiming it can maximize again.
    /// </summary>
    private void SyncMaximizeAffordance()
    {
        var maximized = WindowState == WindowState.Maximized;
        var iconKey = maximized ? "IconRestore" : "IconMaximize";
        var label = maximized ? "Restore" : "Maximize";

        MaximizeButton.Icon = (Controls.IconDefinition)FindResource(iconKey);
        MaximizeButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeButton, label);

        SyncWindowInset();
    }

    /// <summary>
    /// Maximized, the window hangs a resize border off every edge of the
    /// screen and the content goes with it. See <see cref="WindowFrame"/>.
    /// </summary>
    private void SyncWindowInset() => WindowRoot.Margin = WindowFrame.MaximizedInset(this);
}
