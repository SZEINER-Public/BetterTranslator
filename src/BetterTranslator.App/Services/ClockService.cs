using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterTranslator.App.Services;

/// <summary>
/// One timer for the whole application. Every relative timestamp on screen
/// reads <see cref="Now"/>, so a list of a hundred chats still costs one tick
/// every five seconds rather than a hundred timers.
/// </summary>
public sealed partial class ClockService : ObservableObject
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _timer;

    public ClockService()
    {
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Now = DateTimeOffset.Now;
        _timer.Start();
    }

    [ObservableProperty]
    public partial DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
}
