using System.Runtime.Versioning;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.UpdateService;

[SupportedOSPlatform("windows")]
internal sealed class UpdateWorker(UpdatePaths paths, IUpdateLog log)
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private static readonly TimeSpan Jitter = TimeSpan.FromMinutes(25);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly UpdateAnnouncer _announcer = new(paths, log);

    private UpdateStatus? _last;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        log.Write("The update service is running.");

        using var http = GitHubReleaseClient.CreateHttpClient();

        var workflow = new UpdateWorkflow(new GitHubReleaseClient(http, paths, log), http, paths, log);
        var pipe = new UpdaterPipeServer((request, token) => AnswerAsync(workflow, request, token), log);

        var serving = pipe.RunAsync(cancellationToken);

        try
        {
            await Task.Delay(FirstCheck, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await CycleAsync(workflow, cancellationToken).ConfigureAwait(false);
                await Task.Delay(Next(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            log.Write("The update service is stopping.");

            try
            {
                await serving.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<UpdateStatus> CycleAsync(UpdateWorkflow workflow, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var status = await workflow.CheckFetchAndApplyAsync(cancellationToken).ConfigureAwait(false);
            _last = status;

            await _announcer.AnnounceAsync(status, workflow.LastRelease, cancellationToken).ConfigureAwait(false);

            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Write("The update cycle failed and the installed build was left alone", ex);

            var status = UpdateStatus.Failure(workflow.Identity, "The last check did not finish.", workflow.Staged is not null);
            _last = status;

            return status;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<UpdaterResponse> AnswerAsync(
        UpdateWorkflow workflow,
        UpdaterRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Verb)
        {
            case UpdaterVerb.Check:
            {
                var status = await CycleAsync(workflow, cancellationToken).ConfigureAwait(false);

                return new UpdaterResponse(status.Outcome != UpdateOutcome.CheckFailed, status.Detail, status);
            }

            case UpdaterVerb.Apply:
            {
                await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    var status = await workflow.ApplyStagedAsync(cancellationToken).ConfigureAwait(false);
                    _last = status;

                    return new UpdaterResponse(status.Outcome != UpdateOutcome.CheckFailed, status.Detail, status);
                }
                finally
                {
                    _oneAtATime.Release();
                }
            }

            default:
            {
                var status = _last ?? UpdateStatus.Failure(
                    workflow.Identity,
                    "No check has run yet in this session.",
                    workflow.Staged is not null);

                return new UpdaterResponse(true, status.Detail, status);
            }
        }
    }

    private static TimeSpan Next()
    {
        var swing = Random.Shared.NextDouble() * 2 - 1;

        return Period + (Jitter * swing);
    }
}
