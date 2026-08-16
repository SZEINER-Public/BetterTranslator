using System.Windows.Threading;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// The two waits around a loading placeholder, as one reusable thing.
///
/// The delay is what stops work that finishes quickly flashing a placeholder
/// nobody had time to read. The floor is what stops slower work flickering one
/// away the instant it lands. Neither is an animation: nothing moves during
/// them, which is why they are plain millisecond tokens rather than durations.
///
/// <see cref="EntryViewModel"/> still carries its own copy of this, wound
/// through the phase model it also drives. This is the same behaviour with
/// nothing else attached, for callers that only need the placeholder.
/// </summary>
public sealed class SkeletonGate(Action<bool> shows)
{
    private DispatcherTimer? _timer;
    private long _shownAtMs;
    private Action? _held;

    public bool IsShowing { get; private set; }

    /// <summary>
    /// Work has started. The placeholder does not appear yet; it appears if the
    /// work is still running when the delay is up.
    /// </summary>
    public void Begin()
    {
        Stop();
        _held = null;
        Show(false);

        // No token to read means no screen to read it for, so nothing is owed a
        // delay and the result lands as it arrives.
        if (Tokens.TryMilliseconds("SkeletonRevealDelayMs") is { } delay)
        {
            Restart(delay);
        }
    }

    /// <summary>
    /// The placeholder goes up now, with no delay owed.
    ///
    /// For work whose shape is known before it starts. A chat sends its first
    /// message, waits for a translation and only then asks for a name, so the
    /// row is going to be waiting for seconds and there is nothing to protect
    /// against a flash -- what the delay would buy is a few hundred milliseconds
    /// of the truncated source sitting there looking like the chat's name.
    /// </summary>
    public void BeginNow()
    {
        Stop();
        _held = null;
        _shownAtMs = Environment.TickCount64;
        Show(true);
    }

    /// <summary>
    /// Work has finished. <paramref name="apply"/> runs now if the placeholder
    /// never appeared or has had its floor, and otherwise once it has.
    /// </summary>
    public void Settle(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        Stop();
        _held = null;

        if (!IsShowing)
        {
            apply();
            return;
        }

        // Monotonic, so a clock change part way through cannot make the
        // placeholder appear to have been up for hours.
        var shownFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - _shownAtMs);
        var floor = Tokens.TryMilliseconds("SkeletonMinHoldMs") ?? TimeSpan.Zero;

        if (shownFor >= floor)
        {
            Show(false);
            apply();
            return;
        }

        _held = apply;
        Restart(floor - shownFor);
    }

    private void OnTick()
    {
        Stop();

        if (_held is { } apply)
        {
            _held = null;
            Show(false);
            apply();
            return;
        }

        _shownAtMs = Environment.TickCount64;
        Show(true);
    }

    private void Show(bool showing)
    {
        IsShowing = showing;
        shows(showing);
    }

    private void Restart(TimeSpan interval)
    {
        var timer = Timer();
        timer.Stop();
        timer.Interval = interval;
        timer.Start();
    }

    private void Stop() => _timer?.Stop();

    /// <summary>
    /// Built on first use rather than in a constructor: a row has to be
    /// constructible without a running application to read tokens from.
    /// </summary>
    private DispatcherTimer Timer()
    {
        if (_timer is not null)
        {
            return _timer;
        }

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => OnTick();

        return _timer;
    }
}
