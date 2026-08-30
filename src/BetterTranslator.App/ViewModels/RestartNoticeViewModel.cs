using BetterTranslator.Core.Services.Restart;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public enum RestartNoticeState
{
    CountingDown,
    HeldByActiveWork,
    Postponed,
    Restarting,
    Failed,
}

public sealed partial class RestartNoticeViewModel : ObservableObject
{
    public const int DefaultCountdownSeconds = 15;

    private readonly RestartRequest _request;
    private readonly Func<bool, Task<RestartReport>> _restart;
    private readonly Action _dismiss;
    private readonly Action<bool> _remember;

    private bool _fired;

    public RestartNoticeViewModel(
        RestartRequest request,
        string installedSummary,
        IReadOnlyList<string> activeWork,
        Func<bool, Task<RestartReport>> restart,
        Action dismiss,
        Action<bool> remember,
        int countdownSeconds = DefaultCountdownSeconds)
    {
        _request = request;
        _restart = restart;
        _dismiss = dismiss;
        _remember = remember;

        InstalledSummary = installedSummary;
        ActiveWork = activeWork;
        SecondsRemaining = countdownSeconds;
        State = activeWork.Count > 0 ? RestartNoticeState.HeldByActiveWork : RestartNoticeState.CountingDown;
    }

    public string InstalledSummary { get; }

    public IReadOnlyList<string> ActiveWork { get; }

    public string Reason => _request.Reason;

    [ObservableProperty]
    public partial RestartNoticeState State { get; set; }

    [ObservableProperty]
    public partial int SecondsRemaining { get; set; }

    [ObservableProperty]
    public partial string? Failure { get; set; }

    public bool IsCountingDown => State == RestartNoticeState.CountingDown;

    public bool IsHeld => State == RestartNoticeState.HeldByActiveWork;

    public bool HasFailure => Failure is { Length: > 0 };

    public string Title => State switch
    {
        RestartNoticeState.HeldByActiveWork => "Restart when this translation finishes",
        RestartNoticeState.Postponed => "Put off until you say so",
        RestartNoticeState.Restarting => "Restarting",
        RestartNoticeState.Failed => "The restart did not start",
        _ => "Restarting to finish the install",
    };

    public string Body => State switch
    {
        RestartNoticeState.HeldByActiveWork => ActiveWork.Count == 1
            ? $"{InstalledSummary} A translation is still running, so nothing will be restarted until you say so."
            : $"{InstalledSummary} {ActiveWork.Count} translations are still running, so nothing will be restarted until you say so.",
        RestartNoticeState.Postponed => "Nothing will restart. Close and open the app yourself when it suits you.",
        RestartNoticeState.Restarting => "Closing this window and opening it again.",
        RestartNoticeState.Failed => Failure ?? "The new process did not start.",
        _ => $"{InstalledSummary} BetterTranslator will close and open again so the new files are loaded.",
    };

    public string CountdownLabel => SecondsRemaining == 1 ? "1 second" : $"{SecondsRemaining} seconds";

    public bool CanPostpone => State is RestartNoticeState.CountingDown or RestartNoticeState.HeldByActiveWork;

    public void Tick()
    {
        if (State != RestartNoticeState.CountingDown)
        {
            return;
        }

        if (SecondsRemaining > 0)
        {
            SecondsRemaining--;
        }

        if (SecondsRemaining == 0)
        {
            _ = RestartAsync(cancelActiveWork: false);
        }
    }

    [RelayCommand]
    private Task RestartNow() => RestartAsync(cancelActiveWork: false);

    [RelayCommand]
    private Task CancelWorkAndRestart() => RestartAsync(cancelActiveWork: true);

    [RelayCommand]
    private void Postpone()
    {
        State = RestartNoticeState.Postponed;
        _dismiss();
    }

    [RelayCommand]
    private void TurnOff()
    {
        _remember(false);
        _dismiss();
    }

    private async Task RestartAsync(bool cancelActiveWork)
    {
        if (_fired || State == RestartNoticeState.Postponed)
        {
            return;
        }

        _fired = true;
        State = RestartNoticeState.Restarting;

        var report = await _restart(cancelActiveWork).ConfigureAwait(true);

        if (report.Restarted)
        {
            return;
        }

        _fired = false;

        if (report.Outcome == RestartOutcome.BlockedByActiveWork)
        {
            State = RestartNoticeState.HeldByActiveWork;
            Failure = report.Detail;
            return;
        }

        State = RestartNoticeState.Failed;
        Failure = report.Detail;
    }

    partial void OnStateChanged(RestartNoticeState value)
    {
        OnPropertyChanged(nameof(IsCountingDown));
        OnPropertyChanged(nameof(IsHeld));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(CanPostpone));
    }

    partial void OnSecondsRemainingChanged(int value) => OnPropertyChanged(nameof(CountdownLabel));

    partial void OnFailureChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(Body));
    }
}
