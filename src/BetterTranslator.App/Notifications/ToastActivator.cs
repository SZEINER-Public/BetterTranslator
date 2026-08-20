using System.Runtime.InteropServices;

namespace BetterTranslator.App.Notifications;

[ComImport]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback
{
    void Activate(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        IntPtr data,
        uint count);
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

public sealed class ToastActivator : INotificationActivationCallback
{
    private readonly Action<string> _activated;

    public ToastActivator(Action<string> activated) => _activated = activated;

    public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
    {
        if (!string.Equals(appUserModelId, ShellIdentity.AppUserModelId, StringComparison.Ordinal))
        {
            return;
        }

        _activated(invokedArgs ?? string.Empty);
    }
}

public sealed class ToastActivatorHost : IDisposable
{
    private const uint LocalServer = 0x4;
    private const uint MultipleUse = 1;
    private const int NoAggregation = unchecked((int)0x80040110);
    private const int NoInterface = unchecked((int)0x80004002);

    private readonly ActivatorFactory _factory;

    private uint _cookie;

    private ToastActivatorHost(ActivatorFactory factory, uint cookie)
    {
        _factory = factory;
        _cookie = cookie;
    }

    public static ToastActivatorHost? Register(Action<string> activated)
    {
        var factory = new ActivatorFactory(new ToastActivator(activated));
        var clsid = ShellIdentity.Activator;

        try
        {
            var result = CoRegisterClassObject(ref clsid, factory, LocalServer, MultipleUse, out var cookie);

            return result == 0 ? new ToastActivatorHost(factory, cookie) : null;
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_cookie == 0)
        {
            return;
        }

        try
        {
            CoRevokeClassObject(_cookie);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            _cookie = 0;
            GC.KeepAlive(_factory);
        }
    }

    private sealed class ActivatorFactory(INotificationActivationCallback callback) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance)
        {
            instance = IntPtr.Zero;

            if (outer != IntPtr.Zero)
            {
                return NoAggregation;
            }

            var unknown = Marshal.GetIUnknownForObject(callback);

            try
            {
                return Marshal.QueryInterface(unknown, in interfaceId, out instance) == 0 ? 0 : NoInterface;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool locked) => 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        ref Guid classId,
        [MarshalAs(UnmanagedType.IUnknown)] object factory,
        uint context,
        uint flags,
        out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);
}
