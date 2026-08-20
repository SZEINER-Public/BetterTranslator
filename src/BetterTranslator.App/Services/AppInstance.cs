using BetterTranslator.Updates.Ipc;

namespace BetterTranslator.App.Services;

public static class RunningInstance
{
    public const string MutexName = @"Local\BetterTranslator.Instance";

    public static bool Exists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var opened))
            {
                return false;
            }

            opened.Dispose();

            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool Exists() => Exists(MutexName);
}

public sealed class AppInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();

    private Task _serving = Task.CompletedTask;

    public AppInstance(string mutexName = RunningInstance.MutexName)
    {
        _mutex = new Mutex(false, mutexName, out var created);
        IsOnlyInstance = created;
    }

    public bool IsOnlyInstance { get; }

    public void Serve(Func<AppInstanceRequest, CancellationToken, Task<AppInstanceReply>> handle)
    {
        if (!_serving.IsCompleted)
        {
            return;
        }

        _serving = new AppInstanceServer(handle).RunAsync(_stopping.Token);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _mutex.Dispose();
    }
}
