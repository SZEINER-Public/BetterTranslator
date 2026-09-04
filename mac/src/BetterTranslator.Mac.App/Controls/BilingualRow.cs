using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace BetterTranslator.Mac.App.Controls;

public sealed class BilingualRow : TemplatedControl
{
    public static readonly StyledProperty<string> SourceLabelProperty =
        AvaloniaProperty.Register<BilingualRow, string>(nameof(SourceLabel), string.Empty);

    public static readonly StyledProperty<string> TargetLabelProperty =
        AvaloniaProperty.Register<BilingualRow, string>(nameof(TargetLabel), string.Empty);

    public static readonly StyledProperty<object?> SourceContentProperty =
        AvaloniaProperty.Register<BilingualRow, object?>(nameof(SourceContent));

    public static readonly StyledProperty<object?> TargetContentProperty =
        AvaloniaProperty.Register<BilingualRow, object?>(nameof(TargetContent));

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<BilingualRow, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<bool> HasFailedProperty =
        AvaloniaProperty.Register<BilingualRow, bool>(nameof(HasFailed), false);

    public static readonly StyledProperty<string> FailureTextProperty =
        AvaloniaProperty.Register<BilingualRow, string>(nameof(FailureText), string.Empty);

    public static readonly StyledProperty<string?> NoteProperty =
        AvaloniaProperty.Register<BilingualRow, string?>(nameof(Note));

    public static readonly StyledProperty<bool> HasNoteProperty =
        AvaloniaProperty.Register<BilingualRow, bool>(nameof(HasNote), false);

    public static readonly StyledProperty<string> RetryTextProperty =
        AvaloniaProperty.Register<BilingualRow, string>(nameof(RetryText), string.Empty);

    public static readonly StyledProperty<ICommand?> RetryCommandProperty =
        AvaloniaProperty.Register<BilingualRow, ICommand?>(nameof(RetryCommand));

    public static readonly StyledProperty<object?> RetryParameterProperty =
        AvaloniaProperty.Register<BilingualRow, object?>(nameof(RetryParameter));

    public string SourceLabel
    {
        get => GetValue(SourceLabelProperty);
        set => SetValue(SourceLabelProperty, value);
    }

    public string TargetLabel
    {
        get => GetValue(TargetLabelProperty);
        set => SetValue(TargetLabelProperty, value);
    }

    public object? SourceContent
    {
        get => GetValue(SourceContentProperty);
        set => SetValue(SourceContentProperty, value);
    }

    public object? TargetContent
    {
        get => GetValue(TargetContentProperty);
        set => SetValue(TargetContentProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public bool HasFailed
    {
        get => GetValue(HasFailedProperty);
        set => SetValue(HasFailedProperty, value);
    }

    public string FailureText
    {
        get => GetValue(FailureTextProperty);
        set => SetValue(FailureTextProperty, value);
    }

    public string? Note
    {
        get => GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public bool HasNote
    {
        get => GetValue(HasNoteProperty);
        set => SetValue(HasNoteProperty, value);
    }

    public string RetryText
    {
        get => GetValue(RetryTextProperty);
        set => SetValue(RetryTextProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public object? RetryParameter
    {
        get => GetValue(RetryParameterProperty);
        set => SetValue(RetryParameterProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(BilingualRow);
}
