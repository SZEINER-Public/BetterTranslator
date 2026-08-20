using System.IO.Pipes;

namespace BetterTranslator.Updates.Ipc;

public sealed class AppInstanceClient(string? pipeName = null, TimeSpan? connectTimeout = null)
{
    private readonly string _pipeName = pipeName ?? AppInstanceProtocol.PipeName();

    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);

    public string PipeName => _pipeName;

    public async Task<AppInstanceReply?> AskAsync(
        AppInstanceVerb verb,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Length > AppInstanceProtocol.MaxArgumentsLength)
        {
            return null;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            pipe.ReadMode = PipeTransmissionMode.Message;

            await pipe
                .WriteAsync(AppInstanceProtocol.Serialize(new AppInstanceRequest(verb, arguments)), cancellationToken)
                .ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[AppInstanceProtocol.MaxReplyBytes];
            var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read <= 0 || !pipe.IsMessageComplete)
            {
                return null;
            }

            return AppInstanceProtocol.TryParseReply(buffer.AsSpan(0, read), out var reply) ? reply : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        await AskAsync(AppInstanceVerb.Ping, string.Empty, cancellationToken).ConfigureAwait(false) is { Ok: true };
}
