using System.IO.Pipes;
using System.Text;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacSingleInstance(IAppPathsSeam paths) : ISingleInstanceSeam, IDisposable
{
    private const string PipeName = "BetterTranslator.Mac.Activation";

    private FileStream? _lock;
    private CancellationTokenSource? _listener;

    public event Action<IReadOnlyList<string>>? Activated;

    public bool TryClaim(out string reason)
    {
        var file = Path.Combine(paths.Root, "single-instance.lock");

        try
        {
            Directory.CreateDirectory(paths.Root);
            _lock = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            reason = "Another BetterTranslator window is already open.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "The instance lock could not be taken in " + paths.Root + ".";
            return false;
        }

        reason = string.Empty;
        StartListening();

        return true;
    }

    public async Task SignalExistingAsync(IReadOnlyList<string> arguments)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(2000).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', arguments));
            await client.WriteAsync(payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();
        _lock?.Dispose();
    }

    private void StartListening()
    {
        _listener = new CancellationTokenSource();
        var token = _listener.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    Activated?.Invoke(payload.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
        }, token);
    }
}
