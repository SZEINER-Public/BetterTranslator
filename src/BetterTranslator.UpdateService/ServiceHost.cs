using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BetterTranslator.Updates.Logging;

namespace BetterTranslator.UpdateService;

[SupportedOSPlatform("windows")]
internal sealed class ServiceHost
{
    private const uint Win32OwnProcess = 0x00000010;

    private const uint StopPending = 0x00000003;
    private const uint Running = 0x00000004;
    private const uint Stopped = 0x00000001;
    private const uint StartPending = 0x00000002;

    private const uint AcceptStop = 0x00000001;
    private const uint AcceptShutdown = 0x00000004;

    private const uint ControlStop = 0x00000001;
    private const uint ControlShutdown = 0x00000005;
    private const uint ControlInterrogate = 0x00000004;

    private const int NotStartedByController = 1063;

    private static ServiceMainDelegate? _main;
    private static HandlerDelegate? _handler;
    private static ServiceHost? _instance;

    private readonly string _name;
    private readonly Func<CancellationToken, Task> _work;
    private readonly IUpdateLog _log;
    private readonly CancellationTokenSource _stopping = new();

    private IntPtr _status = IntPtr.Zero;
    private uint _checkPoint = 1;

    private ServiceHost(string name, Func<CancellationToken, Task> work, IUpdateLog log)
    {
        _name = name;
        _work = work;
        _log = log;
    }

    public static int Run(string name, Func<CancellationToken, Task> work, IUpdateLog log)
    {
        _instance = new ServiceHost(name, work, log);
        _main = ServiceMain;

        var entries = new ServiceTableEntry[2];
        entries[0] = new ServiceTableEntry
        {
            Name = Marshal.StringToHGlobalUni(name),
            Main = Marshal.GetFunctionPointerForDelegate(_main),
        };
        entries[1] = new ServiceTableEntry { Name = IntPtr.Zero, Main = IntPtr.Zero };

        var size = Marshal.SizeOf<ServiceTableEntry>();
        var table = Marshal.AllocHGlobal(size * entries.Length);

        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                Marshal.StructureToPtr(entries[index], table + (index * size), fDeleteOld: false);
            }

            if (StartServiceCtrlDispatcher(table))
            {
                return 0;
            }

            var error = Marshal.GetLastWin32Error();

            if (error == NotStartedByController)
            {
                log.Write("Started outside the service control manager. Use --console to run it in the foreground.");
            }
            else
            {
                log.Write($"The service control dispatcher refused to start ({error}).");
            }

            return error;
        }
        finally
        {
            Marshal.FreeHGlobal(entries[0].Name);
            Marshal.FreeHGlobal(table);
        }
    }

    private static void ServiceMain(uint count, IntPtr arguments)
    {
        var host = _instance;

        if (host is null)
        {
            return;
        }

        _handler = host.Handle;
        host._status = RegisterServiceCtrlHandlerEx(host._name, _handler, IntPtr.Zero);

        if (host._status == IntPtr.Zero)
        {
            host._log.Write($"The service could not register its control handler ({Marshal.GetLastWin32Error()}).");
            return;
        }

        host.Report(StartPending, TimeSpan.FromSeconds(10));
        host.Report(Running, TimeSpan.Zero);

        try
        {
            host._work(host._stopping.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            host._log.Write("The update service stopped on an unhandled failure", ex);
        }
        finally
        {
            host.Report(Stopped, TimeSpan.Zero);
        }
    }

    private uint Handle(uint control, uint eventType, IntPtr data, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StopPending, TimeSpan.FromSeconds(20));
                _stopping.Cancel();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return 0;
        }
    }

    private void Report(uint state, TimeSpan wait)
    {
        var status = new ServiceStatus
        {
            ServiceType = Win32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = state is Running or Stopped ? 0 : _checkPoint++,
            WaitHint = (uint)wait.TotalMilliseconds,
        };

        SetServiceStatus(_status, ref status);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void ServiceMainDelegate(uint count, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint HandlerDelegate(uint control, uint eventType, IntPtr data, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Main;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceCtrlDispatcherW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(IntPtr table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterServiceCtrlHandlerExW")]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
