using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BetterTranslator.App.Controls;

public sealed class BilingualRow : Control
{
    public static readonly DependencyProperty SourceLabelProperty = DependencyProperty.Register(
        nameof(SourceLabel), typeof(string), typeof(BilingualRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TargetLabelProperty = DependencyProperty.Register(
        nameof(TargetLabel), typeof(string), typeof(BilingualRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SourceContentProperty = DependencyProperty.Register(
        nameof(SourceContent), typeof(object), typeof(BilingualRow), new PropertyMetadata(null));

    public static readonly DependencyProperty TargetContentProperty = DependencyProperty.Register(
        nameof(TargetContent), typeof(object), typeof(BilingualRow), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(BilingualRow), new PropertyMetadata(null));

    public static readonly DependencyProperty HasFailedProperty = DependencyProperty.Register(
        nameof(HasFailed), typeof(bool), typeof(BilingualRow), new PropertyMetadata(false));

    public static readonly DependencyProperty FailureTextProperty = DependencyProperty.Register(
        nameof(FailureText), typeof(string), typeof(BilingualRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(BilingualRow), new PropertyMetadata(null));

    public static readonly DependencyProperty HasNoteProperty = DependencyProperty.Register(
        nameof(HasNote), typeof(bool), typeof(BilingualRow), new PropertyMetadata(false));

    public static readonly DependencyProperty RetryTextProperty = DependencyProperty.Register(
        nameof(RetryText), typeof(string), typeof(BilingualRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(BilingualRow), new PropertyMetadata(null));

    public static readonly DependencyProperty RetryParameterProperty = DependencyProperty.Register(
        nameof(RetryParameter), typeof(object), typeof(BilingualRow), new PropertyMetadata(null));

    public string SourceLabel
    {
        get => (string)GetValue(SourceLabelProperty);
        set => SetValue(SourceLabelProperty, value);
    }

    public string TargetLabel
    {
        get => (string)GetValue(TargetLabelProperty);
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
        get => (bool)GetValue(HasFailedProperty);
        set => SetValue(HasFailedProperty, value);
    }

    public string FailureText
    {
        get => (string)GetValue(FailureTextProperty);
        set => SetValue(FailureTextProperty, value);
    }

    public string? Note
    {
        get => (string?)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public bool HasNote
    {
        get => (bool)GetValue(HasNoteProperty);
        set => SetValue(HasNoteProperty, value);
    }

    public string RetryText
    {
        get => (string)GetValue(RetryTextProperty);
        set => SetValue(RetryTextProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public object? RetryParameter
    {
        get => GetValue(RetryParameterProperty);
        set => SetValue(RetryParameterProperty, value);
    }
}
