using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.App.Views;

public partial class ChatView : UserControl
{
    private ChatWorkspaceViewModel? _workspace;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// A drag is a drop target only if it carries a file this chat can read, so
    /// the cursor says no while the pointer is still moving rather than after it
    /// is released. Effects are set on every DragOver, not only DragEnter, or
    /// Windows falls back to its own guess partway across.
    /// </summary>
    private void OnDragOverChat(object sender, DragEventArgs e)
    {
        var readable = Dropped(e);

        e.Effects = readable.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (_workspace is null)
        {
            return;
        }

        var wasTarget = _workspace.IsDropTarget;
        _workspace.IsDropTarget = readable.Count > 0;

        // Only on the crossing, not on every DragOver: this fires many times a
        // second while the pointer moves, and restarting the animation on each
        // would hold the mark at its first frame.
        if (!wasTarget && _workspace.IsDropTarget)
        {
            RaiseDropMark();
        }
    }

    /// <summary>
    /// The mark arrives rather than appearing: it fades up and rises the item
    /// travel distance, decelerating. One pass, no loop -- an infinite nudge
    /// would be decoration that never stops asking for attention.
    /// </summary>
    private void RaiseDropMark()
    {
        var rise = new TranslateTransform();
        DropMark.RenderTransform = rise;

        var duration = Tokens.Get<Duration>("Motion200");
        var easing = Tokens.Get<KeySpline>("EaseStandard");

        DropMark.Opacity = 0;
        rise.Y = Tokens.Number("DropMarkTravel");

        MotionService.AnimateDouble(DropMark, OpacityProperty, 1, duration, easing);
        MotionService.AnimateDouble(rise, TranslateTransform.YProperty, 0, duration, easing);
    }

    private void OnDragLeaveChat(object sender, DragEventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.IsDropTarget = false;
        }
    }

    private async void OnDropChat(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_workspace is null)
        {
            return;
        }

        _workspace.IsDropTarget = false;

        // Hashing reads the files, so the drop returns before they are all in.
        await _workspace.AttachAsync(Dropped(e), CancellationToken.None);
    }

    /// <summary>
    /// The readable paths a drag carries. Anything that is not a file drop at
    /// all -- dragged text, a browser selection -- reads as nothing.
    /// </summary>
    private static IReadOnlyList<string> Dropped(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
            ? ChatWorkspaceViewModel.Readable(e.Data.GetData(DataFormats.FileDrop) as string[])
            : [];

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.ComposerFocusRequested -= FocusComposer;
        }

        _workspace = DataContext as ChatWorkspaceViewModel;

        if (_workspace is not null)
        {
            _workspace.ComposerFocusRequested += FocusComposer;
        }
    }

    private void FocusComposer() => ComposerInput.Focus();

    /// <summary>Enter commits the rename, Escape cancels it.</summary>
    private void OnChatNameKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _workspace?.CommitRenameCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _workspace?.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Losing focus commits, the same as Enter.</summary>
    private void OnChatNameLostFocus(object sender, RoutedEventArgs e) =>
        _workspace?.CommitRenameCommand.Execute(null);

    private void OnChatNameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ChatNameEditor.Focus();
            ChatNameEditor.SelectAll();
        }
    }
}
