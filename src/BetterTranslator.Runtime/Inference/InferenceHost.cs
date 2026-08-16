using System.Diagnostics;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// Owns the local runtime process and the endpoint that talks to it. The
/// process is started on demand against a chosen model and stopped when the
/// application closes; nothing here blocks the UI thread.
/// </summary>
public sealed class InferenceHost(InstallPaths paths) : IAsyncDisposable
{
    /// <summary>
    /// Loopback only. The runtime is local by design and must not be reachable
    /// from anywhere else on the network.
    /// </summary>
    private const string Host = "127.0.0.1";

    private const int Port = 8724;

    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri($"http://{Host}:{Port}"),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private Process? _process;

    public InferenceEndpoint Endpoint => new(_httpClient);

    public ModelComponent? LoadedModel { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Path to the runtime executable inside the models folder. Null until the
    /// runtime is installed, which is what gates every route needing a model.
    /// </summary>
    public string? RuntimeExecutable
    {
        get
        {
            var candidate = Path.Combine(paths.ModelsFolder, "betterruntime", "betterruntime.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>
    /// Starts the runtime against a model, or does nothing when that model is
    /// already loaded. Returns false when the runtime or the model is missing,
    /// so the caller can route to the download manager instead of throwing.
    /// </summary>
    public async Task<bool> StartAsync(ModelComponent model, CancellationToken cancellationToken)
    {
        if (IsRunning && LoadedModel?.Id == model.Id)
        {
            return true;
        }

        var executable = RuntimeExecutable;
        if (executable is null || !paths.IsInstalled(model))
        {
            return false;
        }

        await StopAsync().ConfigureAwait(false);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(paths.PathFor(model));
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(Host);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(Port.ToString());
        start.ArgumentList.Add("--embedding");

        _process = Process.Start(start);
        LoadedModel = model;

        return await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            _process.Dispose();
            _process = null;
            LoadedModel = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    /// <summary>
    /// Polls the health endpoint until the model is loaded. A large model takes
    /// a while to map, so this is generous but bounded.
    /// </summary>
    private async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var endpoint = Endpoint;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                return false;
            }

            if (await endpoint.IsReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
