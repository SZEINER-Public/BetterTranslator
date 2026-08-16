namespace BetterTranslator.Tests;

/// <summary>
/// Captures progress reports on the calling thread, in order.
///
/// <see cref="Progress{T}"/> posts to the captured SynchronizationContext, and
/// a test has none, so it posts to the thread pool instead: reports can then
/// arrive after the awaited call has returned, or out of order. Asserting on
/// the first and last report needs both guarantees.
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];
    private readonly Action<T>? _onReport;

    public SyncProgress(Action<T>? onReport = null) => _onReport = onReport;

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(T value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }

        _onReport?.Invoke(value);
    }
}
