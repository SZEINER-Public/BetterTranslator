using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterTranslator.Updates.Logging;

namespace BetterTranslator.Updates.Ipc;

[SupportedOSPlatform("windows")]
public sealed class UpdaterPipeServer(
    Func<UpdaterRequest, CancellationToken, Task<UpdaterResponse>> handle,
    IUpdateLog? log = null)
{
    private const int MaxInstances = 4;

    private readonly IUpdateLog _log = log ?? NullUpdateLog.Instance;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log.Write("A request over the updater pipe was dropped", ex);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ServeOneAsync(CancellationToken cancellationToken)
    {
        await using var pipe = NamedPipeServerStreamAcl.Create(
            UpdaterProtocol.PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            UpdaterProtocol.MaxRequestBytes,
            UpdaterProtocol.MaxResponseBytes,
            Security());

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await AnswerAsync(pipe, cancellationToken).ConfigureAwait(false);
            var payload = UpdaterProtocol.Serialize(response);

            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
    }

    private async Task<UpdaterResponse> AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[UpdaterProtocol.MaxRequestBytes];
        var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read <= 0)
        {
            return UpdaterResponse.Refused("The request was empty.");
        }

        if (!pipe.IsMessageComplete)
        {
            await DrainAsync(pipe, cancellationToken).ConfigureAwait(false);

            return UpdaterResponse.Refused("The request is not a size this accepts.");
        }

        if (!UpdaterProtocol.TryParseRequest(buffer.AsSpan(0, read), out var request, out var refusal))
        {
            return UpdaterResponse.Refused(refusal);
        }

        try
        {
            return await handle(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Write($"The {UpdaterProtocol.Name(request.Verb)} request failed", ex);

            return UpdaterResponse.Refused("That request could not be completed.");
        }
    }

    private static async Task DrainAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var scratch = new byte[UpdaterProtocol.MaxRequestBytes];

        while (!pipe.IsMessageComplete)
        {
            var read = await pipe.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                return;
            }
        }
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
