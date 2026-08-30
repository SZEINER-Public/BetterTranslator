namespace BetterTranslator.Core.Services.Restart;

public sealed record RestartAttempt(string BatchId, string Reason, DateTimeOffset When);

public sealed class RestartGuard
{
    private readonly Lock _gate = new();
    private readonly List<RestartAttempt> _spent = [];
    private readonly Func<DateTimeOffset> _clock;

    public RestartGuard()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    public RestartGuard(Func<DateTimeOffset> clock) => _clock = clock;

    public IReadOnlyList<RestartAttempt> Spent
    {
        get
        {
            lock (_gate)
            {
                return [.. _spent];
            }
        }
    }

    public string? LastRefusal { get; private set; }

    public bool IsSpent(string batchId)
    {
        lock (_gate)
        {
            return _spent.Any(a => string.Equals(a.BatchId, batchId, StringComparison.Ordinal));
        }
    }

    public bool Claim(string? batchId, string reason)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            LastRefusal = "No install batch was named, so nothing has completed that would justify a restart.";
            return false;
        }

        lock (_gate)
        {
            if (_spent.FirstOrDefault(a => string.Equals(a.BatchId, batchId, StringComparison.Ordinal)) is { } already)
            {
                LastRefusal =
                    $"Install batch {batchId} already spent its restart at {already.When:u} for: {already.Reason}. "
                    + "A second restart needs another completed install behind it.";

                return false;
            }

            _spent.Add(new RestartAttempt(batchId, reason, _clock()));
        }

        LastRefusal = null;
        return true;
    }
}
