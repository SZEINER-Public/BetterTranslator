using System.IO.Pipes;

namespace BetterTranslator.Updates.Ipc;

public sealed class UpdaterPipeClient(string pipeName = UpdaterProtocol.PipeName)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<UpdaterResponse?> AskAsync(UpdaterVerb verb, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            pipe.ReadMode = PipeTransmissionMode.Message;

            await pipe.WriteAsync(UpdaterProtocol.Serialize(new UpdaterRequest(verb)), cancellationToken)
                .ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[UpdaterProtocol.MaxResponseBytes];
            var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read <= 0 || !pipe.IsMessageComplete)
            {
                return null;
            }

            return UpdaterProtocol.TryParseResponse(buffer.AsSpan(0, read), out var response) ? response : null;
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
}
