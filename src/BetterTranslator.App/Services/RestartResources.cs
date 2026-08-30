using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BetterTranslator.Core.Services.Restart;
using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.App.Services;

/// <summary>
/// What a restart has to wait for. Both halves count: the window's own
/// generation and whatever an agent started over the loopback listener, since
/// either is a translation somebody is waiting on.
/// </summary>
public sealed class ShellActiveWork(LocalTranslator translator, Mcp.McpServerHost agents) : IActiveWork
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    public IReadOnlyList<string> Running()
    {
        var running = new List<string>();

        if (translator.IsGenerating)
        {
            running.Add("the translation in this window");
        }

        running.AddRange(agents.RunningJobs());

        return running;
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        translator.CancelGeneration();
        agents.CancelRunningJobs();

        while (Running().Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class NamedRestartResource(string name, Func<CancellationToken, Task> release) : IRestartResource
{
    public string Name { get; } = name;

    public Task ReleaseAsync(CancellationToken cancellationToken) => release(cancellationToken);
}

public static class RestartLauncher
{
    public const string RelaunchedMarker = "BETTERTRANSLATOR_RELAUNCHED";

    public static bool Start(RelaunchTarget target)
    {
        if (!target.IsResolved)
        {
            return false;
        }

        var start = new ProcessStartInfo(target.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(target.WorkingDirectory)
                ? target.WorkingDirectory
                : Path.GetDirectoryName(target.Executable) ?? string.Empty,
        };

        foreach (var argument in target.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[RelaunchedMarker] = "1";

        try
        {
            using var started = Process.Start(start);

            return started is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

public sealed class RestartLog
{
    private readonly string _file;
    private readonly Lock _gate = new();

    public RestartLog(string folder)
    {
        Folder = folder;
        _file = Path.Combine(folder, "restart.log");
    }

    public string Folder { get; }

    public string File => _file;

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}{Environment.NewLine}";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                System.IO.File.AppendAllText(_file, line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
