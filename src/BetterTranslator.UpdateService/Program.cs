using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Logging;
using BetterTranslator.UpdateService;

if (!OperatingSystem.IsWindows())
{
    return 1;
}

var paths = new UpdatePaths();
var log = new RollingFileLog(paths.LogFolder);
var worker = new UpdateWorker(paths, log);

if (args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)))
{
    using var stopping = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    try
    {
        await worker.RunAsync(stopping.Token);
    }
    catch (OperationCanceledException)
    {
    }

    return 0;
}

return ServiceHost.Run(UpdatePaths.ServiceName, worker.RunAsync, log);
