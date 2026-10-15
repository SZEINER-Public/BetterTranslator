using BetterTranslator.Runtime.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public readonly record struct DictationInsertion(string Draft, int Caret);

public sealed partial class DictationViewModel : ObservableObject, IDisposable
{
    private static readonly Lazy<RecognitionAvailability> DefaultAvailability =
        new(() => SpeechSupport.ProbeRecognition());

    private readonly IntPtr _recognizer;
    private DictationController? _controller;

    public DictationViewModel(IntPtr recognizer = default)
    {
        _recognizer = recognizer;
        var availability = recognizer == IntPtr.Zero
            ? DefaultAvailability.Value
            : SpeechSupport.ProbeRecognition(recognizer);

        IsAvailable = availability.CanRecognize;
        UnavailableReason = availability.Reason;
    }

    public Func<CancellationToken, Task<DictationOptions>>? Options { get; set; }

    public event EventHandler<string>? InsertRequested;

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial string UnavailableReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsListening { get; set; }

    [ObservableProperty]
    public partial string ProvisionalText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastError { get; set; } = string.Empty;

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(ButtonToolTip));

    partial void OnUnavailableReasonChanged(string value) => OnPropertyChanged(nameof(ButtonToolTip));

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonToolTip));
        OnPropertyChanged(nameof(HasProvisional));
    }

    partial void OnProvisionalTextChanged(string value) => OnPropertyChanged(nameof(HasProvisional));

    public bool HasProvisional => IsListening && ProvisionalText.Length > 0;

    public string ButtonToolTip => !IsAvailable
        ? UnavailableReason
        : IsListening
            ? "Stop dictating. What was heard so far stays in the composer."
            : "Dictate into the composer. Nothing is sent until you send it.";

    public static DictationInsertion Insert(string draft, int selectionStart, int selectionLength, string text)
    {
        draft ??= string.Empty;
        var start = Math.Clamp(selectionStart, 0, draft.Length);
        var end = Math.Clamp(start + Math.Max(selectionLength, 0), start, draft.Length);

        if (string.IsNullOrEmpty(text))
        {
            return new DictationInsertion(draft, start);
        }

        var before = draft[..start];
        var after = draft[end..];
        var insert = text;

        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && !char.IsWhiteSpace(insert[0]))
        {
            insert = " " + insert;
        }

        if (after.Length > 0 && !char.IsWhiteSpace(after[0]) && !char.IsWhiteSpace(insert[^1])
            && !char.IsPunctuation(after[0]))
        {
            insert += " ";
        }

        return new DictationInsertion(before + insert + after, start + insert.Length);
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (IsListening)
        {
            if (_controller is not null)
            {
                await _controller.StopAsync();
            }

            return;
        }

        LastError = string.Empty;

        try
        {
            var options = Options is null
                ? new DictationOptions { Language = "en" }
                : await Options(CancellationToken.None);

            _controller ??= CreateController();
            _controller.Start(options);
            IsListening = true;
        }
        catch (Exception ex) when (ex is SpeechException or InvalidOperationException)
        {
            LastError = ex.Message;
            IsListening = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _controller?.Cancel();
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
    }

    private DictationController CreateController()
    {
        var controller = new DictationController(_recognizer);
        controller.ProvisionalChanged += (_, text) => ProvisionalText = text;
        controller.Committed += (_, commit) => InsertRequested?.Invoke(this, commit.Text);
        controller.Stopped += (_, stopped) =>
        {
            IsListening = false;
            ProvisionalText = string.Empty;

            if (stopped.Status is not (SpeechStatus.Ok or SpeechStatus.Cancelled))
            {
                LastError = stopped.Message;
            }
        };

        return controller;
    }
}
