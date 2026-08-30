namespace BetterTranslator.Core.Services.Restart;

public enum RestartOutcome
{
    Ready,
    Relaunched,
    BlockedByActiveWork,
    RefusedByGuard,
    RefusedBySetting,
    NoRelaunchTarget,
    RelaunchFailed,
}

public sealed record RestartRequest
{
    public required string BatchId { get; init; }

    public required string Reason { get; init; }

    public bool CancelActiveWork { get; init; }
}

public sealed record RestartReport
{
    public required RestartOutcome Outcome { get; init; }

    public required string Detail { get; init; }

    public IReadOnlyList<string> ActiveWork { get; init; } = [];

    public RelaunchTarget? Target { get; init; }

    public IReadOnlyList<string> Released { get; init; } = [];

    public bool Restarted => Outcome == RestartOutcome.Relaunched;
}

public interface IRestartResource
{
    string Name { get; }

    Task ReleaseAsync(CancellationToken cancellationToken);
}

public interface IActiveWork
{
    IReadOnlyList<string> Running();

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed class RestartService
{
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<IRestartResource> _resources;
    private readonly IActiveWork _work;
    private readonly RestartGuard _guard;
    private readonly Func<RelaunchTarget> _resolve;
    private readonly Func<RelaunchTarget, bool> _launch;
    private readonly Action<int> _exit;
    private readonly Action<string> _log;
    private readonly Func<bool> _enabled;

    public RestartService(
        IReadOnlyList<IRestartResource> resources,
        IActiveWork work,
        RestartGuard guard,
        Func<RelaunchTarget> resolve,
        Func<RelaunchTarget, bool> launch,
        Action<int> exit,
        Action<string> log,
        Func<bool> enabled)
    {
        _resources = resources;
        _work = work;
        _guard = guard;
        _resolve = resolve;
        _launch = launch;
        _exit = exit;
        _log = log;
        _enabled = enabled;
    }

    public TimeSpan CancelTimeout { get; init; } = CancelWait;

    public RestartReport Inspect(RestartRequest request)
    {
        if (!_enabled())
        {
            return new RestartReport
            {
                Outcome = RestartOutcome.RefusedBySetting,
                Detail = "Restarting after an install is switched off in Settings.",
            };
        }

        if (_guard.IsSpent(request.BatchId) || string.IsNullOrWhiteSpace(request.BatchId))
        {
            return new RestartReport
            {
                Outcome = RestartOutcome.RefusedByGuard,
                Detail = SpentDetail(request.BatchId),
            };
        }

        var running = _work.Running();

        return running.Count > 0
            ? new RestartReport
            {
                Outcome = RestartOutcome.BlockedByActiveWork,
                Detail = running.Count == 1
                    ? "A translation is still running."
                    : $"{running.Count} translations are still running.",
                ActiveWork = running,
            }
            : new RestartReport
            {
                Outcome = RestartOutcome.Ready,
                Detail = "Nothing is running, so a restart can proceed.",
            };
    }

    public async Task<RestartReport> RestartAsync(RestartRequest request, CancellationToken cancellationToken)
    {
        if (!_enabled())
        {
            _log("Restart refused: the setting is off.");

            return new RestartReport
            {
                Outcome = RestartOutcome.RefusedBySetting,
                Detail = "Restarting after an install is switched off in Settings.",
            };
        }

        var running = _work.Running();

        if (running.Count > 0 && !request.CancelActiveWork)
        {
            _log($"Restart held: {running.Count} translation(s) still running and no cancellation was asked for.");

            return new RestartReport
            {
                Outcome = RestartOutcome.BlockedByActiveWork,
                Detail = "A translation is still running. Restarting would throw away work nobody agreed to lose.",
                ActiveWork = running,
            };
        }

        if (!_guard.Claim(request.BatchId, request.Reason))
        {
            _log("Restart refused by the loop guard: " + _guard.LastRefusal);

            return new RestartReport
            {
                Outcome = RestartOutcome.RefusedByGuard,
                Detail = _guard.LastRefusal ?? SpentDetail(request.BatchId),
            };
        }

        if (running.Count > 0)
        {
            _log($"Cancelling {running.Count} running translation(s) at the reader's request.");
            await CancelAsync(cancellationToken).ConfigureAwait(false);
        }

        var target = _resolve();

        _log("Resolved " + target.Detail);

        if (!target.IsResolved)
        {
            return new RestartReport
            {
                Outcome = RestartOutcome.NoRelaunchTarget,
                Detail = target.Detail,
                Target = target,
            };
        }

        var released = new List<string>();

        foreach (var resource in _resources)
        {
            try
            {
                await resource.ReleaseAsync(cancellationToken).ConfigureAwait(false);
                released.Add(resource.Name);
                _log($"Released {resource.Name}.");
            }
            catch (Exception failure)
            {
                _log($"Releasing {resource.Name} failed: {failure.Message}");
            }
        }

        if (!_launch(target))
        {
            _log("The relaunch did not start. The old process stays up rather than leaving nothing running.");

            return new RestartReport
            {
                Outcome = RestartOutcome.RelaunchFailed,
                Detail = $"Windows did not start {target.Executable}.",
                Target = target,
                Released = released,
            };
        }

        _log($"Relaunched as {target.CommandLine} in {target.WorkingDirectory}. Exiting with 0.");
        _exit(0);

        return new RestartReport
        {
            Outcome = RestartOutcome.Relaunched,
            Detail = $"Restarted for: {request.Reason}.",
            Target = target,
            Released = released,
        };
    }

    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(CancelTimeout);

        try
        {
            await _work.CancelAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log("The running translations did not stop within the cancellation window.");
        }
    }

    private string SpentDetail(string batchId) => string.IsNullOrWhiteSpace(batchId)
        ? "No install batch was named, so nothing has completed that would justify a restart."
        : $"Install batch {batchId} has already had its restart.";
}
