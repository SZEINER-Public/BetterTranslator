using BetterTranslator.App.Services;
using BetterTranslator.Core.Services;
using BetterTranslator.Mac.Seams;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;

namespace BetterTranslator.Mac.Platform;

public sealed class MacUpdateTrigger : IUpdateTriggerSeam
{
    public bool IsSupported => false;

    public string UnsupportedReason =>
        "Updates are installed by replacing the application in Applications. There is no background installer on this platform yet.";

    public Task<bool> RequestAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed class MacUpdaterHost(IUpdateTriggerSeam trigger) : IUpdaterHost
{
    public BuildIdentity Installed { get; } = BuildIdentity.Current;

    public ServiceState State() => ServiceState.NotInstalled;

    public Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(UpdateStatus.Failure(Installed, trigger.UnsupportedReason, ready: false));

    public Task<UpdateStatus> FetchAsync(CancellationToken cancellationToken, IProgress<double>? progress = null) =>
        Task.FromResult(UpdateStatus.Failure(Installed, trigger.UnsupportedReason, ready: false));

    public Task<UpdateStatus?> ReadyAsync(CancellationToken cancellationToken) =>
        Task.FromResult<UpdateStatus?>(null);

    public Task<ElevationOutcome> EnableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ElevationOutcome(false, trigger.UnsupportedReason));

    public Task<ElevationOutcome> DisableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ElevationOutcome(false, trigger.UnsupportedReason));

    public Task<ElevationOutcome> HandOverToStagedBuildAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ElevationOutcome(false, trigger.UnsupportedReason));
}
