using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.Mac.App.Views;

public partial class ChatView : UserControl
{
    private ChatWorkspaceViewModel? _workspace;
    private readonly TextBox? _composerInput;
    private readonly Control? _dropMark;

    public ChatView()
    {
        InitializeComponent();
        _composerInput = this.FindControl<TextBox>("ComposerInput");
        _dropMark = this.FindControl<Control>("DropMark");
        AddHandler(DragDrop.DragOverEvent, OnDragOverChat);
        AddHandler(DragDrop.DragEnterEvent, OnDragOverChat);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeaveChat);
        AddHandler(DragDrop.DropEvent, OnDropChat);
        _composerInput?.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ChatWorkspaceViewModel? Workspace => DataContext as ChatWorkspaceViewModel;

    private void OnDragOverChat(object? sender, DragEventArgs e)
    {
        var readable = Dropped(e);

        e.DragEffects = readable.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (_workspace is null)
        {
            return;
        }

        var wasTarget = _workspace.IsDropTarget;
        _workspace.IsDropTarget = readable.Count > 0;

        if (!wasTarget && _workspace.IsDropTarget)
        {
            RaiseDropMark();
        }
    }

    private void RaiseDropMark()
    {
        var duration = this.FindResource("Motion200") as TimeSpan? ?? TimeSpan.FromMilliseconds(200);
        var easing = this.FindResource("EaseStandard") as Easing ?? new LinearEasing();
        var travel = this.FindResource("DropMarkTravel") as double? ?? 0d;

        var animation = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, travel),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d),
                    },
                },
            },
        };

        if (_dropMark is not null)
        {
            _ = animation.RunAsync(_dropMark);
        }
    }

    private void OnDragLeaveChat(object? sender, DragEventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.IsDropTarget = false;
        }
    }

    private async void OnDropChat(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_workspace is null)
        {
            return;
        }

        _workspace.IsDropTarget = false;

        await _workspace.AttachAsync(Dropped(e), CancellationToken.None);
    }

    private static IReadOnlyList<string> Dropped(DragEventArgs e)
    {
        var files = e.Data.GetFiles();

        if (files is null)
        {
            return [];
        }

        var paths = files
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        return ChatWorkspaceViewModel.Readable(paths);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
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

    private void FocusComposer() => _composerInput?.Focus();

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        var workspace = Workspace;

        if (workspace is not null && workspace.SendCommand.CanExecute(null))
        {
            workspace.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnChatNameKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Workspace?.CommitRenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Workspace?.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnChatNameLostFocus(object? sender, RoutedEventArgs e) =>
        Workspace?.CommitRenameCommand.Execute(null);

    private void OnMemoryWordScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            Workspace?.CloseMemoryWordCommand.Execute(null);
            e.Handled = true;
        }
    }
}
