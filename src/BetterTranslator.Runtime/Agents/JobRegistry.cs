using System.Collections.Concurrent;

namespace BetterTranslator.Runtime.Agents;

public enum JobState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed record JobSnapshot(
    string Id,
    string Kind,
    JobState State,
    int Done,
    int Total,
    string? Detail,
    IReadOnlyList<FileTranslation> Results,
    string? Error)
{
    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;
}

public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, TrackedJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _worker = new(1, 1);

    private int _next;

    public string Start(string kind, int total, Func<TrackedJob, CancellationToken, Task> work)
    {
        var id = "job_" + Interlocked.Increment(ref _next).ToString("D4");
        var job = new TrackedJob(id, kind, total);

        _jobs[id] = job;

        _ = Task.Run(async () =>
        {
            await _worker.WaitAsync(job.Token).ConfigureAwait(false);

            try
            {
                job.Begin();
                await work(job, job.Token).ConfigureAwait(false);
                job.Finish();
            }
            catch (OperationCanceledException)
            {
                job.Cancelled();
            }
            catch (Exception ex)
            {
                job.Fail(ex.Message);
            }
            finally
            {
                _worker.Release();
            }
        });

        return id;
    }

    public JobSnapshot? Status(string id) => _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    public IReadOnlyList<JobSnapshot> All() => [.. _jobs.Values.Select(j => j.Snapshot())];

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        job.Cancel();
        return true;
    }
}

public sealed class TrackedJob
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<FileTranslation> _results = [];
    private readonly Lock _gate = new();

    private JobState _state = JobState.Queued;
    private int _done;
    private string? _detail;
    private string? _error;

    internal TrackedJob(string id, string kind, int total)
    {
        Id = id;
        Kind = kind;
        Total = total;
    }

    public string Id { get; }

    public string Kind { get; }

    public int Total { get; private set; }

    public CancellationToken Token => _cancellation.Token;

    public void Report(int done, string? detail)
    {
        lock (_gate)
        {
            _done = done;
            _detail = detail;
        }
    }

    public void Add(FileTranslation result)
    {
        lock (_gate)
        {
            _results.Add(result);
            _done = _results.Count;
        }
    }

    public void Retotal(int total)
    {
        lock (_gate)
        {
            Total = total;
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new JobSnapshot(Id, Kind, _state, _done, Total, _detail, [.. _results], _error);
        }
    }

    internal void Begin()
    {
        lock (_gate)
        {
            if (_state == JobState.Queued)
            {
                _state = JobState.Running;
            }
        }
    }

    internal void Finish()
    {
        lock (_gate)
        {
            if (_state == JobState.Running)
            {
                _state = _results.Count == 0 || _results.All(r => r.Status != "ok")
                    ? JobState.Failed
                    : JobState.Done;
            }
        }
    }

    internal void Fail(string message)
    {
        lock (_gate)
        {
            _state = JobState.Failed;
            _error = message;
        }
    }

    internal void Cancelled()
    {
        lock (_gate)
        {
            _state = JobState.Cancelled;
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (_state is JobState.Done or JobState.Failed)
            {
                return;
            }
        }

        _cancellation.Cancel();
    }
}
