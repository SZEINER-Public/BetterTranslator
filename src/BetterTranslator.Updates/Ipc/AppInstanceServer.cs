using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterTranslator.Updates.Ipc;

[SupportedOSPlatform("windows")]
public sealed class AppInstanceServer(
    Func<AppInstanceRequest, CancellationToken, Task<AppInstanceReply>> handle,
    string? pipeName = null)
{
    private const int MaxInstances = 4;

    private readonly string _pipeName = pipeName ?? AppInstanceProtocol.PipeName();

    public string PipeName => _pipeName;

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
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            AppInstanceProtocol.MaxRequestBytes,
            AppInstanceProtocol.MaxReplyBytes,
            Security());

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reply = await AnswerAsync(pipe, cancellationToken).ConfigureAwait(false);

            await pipe.WriteAsync(AppInstanceProtocol.Serialize(reply), cancellationToken).ConfigureAwait(false);
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

    private async Task<AppInstanceReply> AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[AppInstanceProtocol.MaxRequestBytes];
        var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read <= 0)
        {
            return AppInstanceReply.Refused("The request was empty.");
        }

        if (!pipe.IsMessageComplete)
        {
            var scratch = new byte[AppInstanceProtocol.MaxRequestBytes];

            while (!pipe.IsMessageComplete)
            {
                if (await pipe.ReadAsync(scratch, cancellationToken).ConfigureAwait(false) <= 0)
                {
                    break;
                }
            }

            return AppInstanceReply.Refused("The request is not a size this accepts.");
        }

        if (!AppInstanceProtocol.TryParseRequest(buffer.AsSpan(0, read), out var request, out var refusal))
        {
            return AppInstanceReply.Refused(refusal);
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
            return AppInstanceReply.Refused("That request could not be completed.");
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

        if (WindowsIdentity.GetCurrent().User is { } owner)
        {
            security.AddAccessRule(new PipeAccessRule(
                owner,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        return security;
    }
}
