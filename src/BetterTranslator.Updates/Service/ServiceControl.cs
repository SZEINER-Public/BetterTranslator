using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterTranslator.Updates.Service;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Paused,
    Unreadable,
}

public sealed record ServiceOutcome(bool Ok, string Detail)
{
    public static ServiceOutcome Failed(string detail) => new(false, detail);

    public static ServiceOutcome Done(string detail) => new(true, detail);
}

public static class ServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;

    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;

    private const uint ConfigDescription = 1;
    private const uint ConfigFailureActions = 2;
    private const uint ConfigDelayedAutoStart = 3;

    private const uint ControlStop = 0x00000001;

    private const uint ActionRestart = 1;

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceExists = 1073;
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    public static ServiceState Query(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);

        if (manager == IntPtr.Zero)
        {
            return ServiceState.Unreadable;
        }

        try
        {
            var service = OpenService(manager, name, ServiceQueryStatus);

            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? ServiceState.NotInstalled
                    : ServiceState.Unreadable;
            }

            try
            {
                return QueryServiceStatus(service, out var status)
                    ? Map(status.CurrentState)
                    : ServiceState.Unreadable;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static ServiceOutcome Install(string name, string displayName, string description, string binaryPath)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);

        if (manager == IntPtr.Zero)
        {
            return ServiceOutcome.Failed(Explain("open the service database", Marshal.GetLastWin32Error()));
        }

        try
        {
            var command = "\"" + binaryPath + "\"";

            var service = CreateService(
                manager,
                name,
                displayName,
                ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus | ServiceStart | ServiceStop,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                command,
                null,
                IntPtr.Zero,
                null,
                null,
                null);

            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();

                return error == ErrorServiceExists
                    ? Reconfigure(manager, name, description, command)
                    : ServiceOutcome.Failed(Explain("register the updater", error));
            }

            try
            {
                Describe(service, description);
                DelayStart(service);
                RestartOnFailure(service);

                return ServiceOutcome.Done("The updater is registered.");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static ServiceOutcome Start(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);

        if (manager == IntPtr.Zero)
        {
            return ServiceOutcome.Failed(Explain("open the service database", Marshal.GetLastWin32Error()));
        }

        try
        {
            var service = OpenService(manager, name, ServiceStart | ServiceQueryStatus);

            if (service == IntPtr.Zero)
            {
                return ServiceOutcome.Failed(Explain("open the updater", Marshal.GetLastWin32Error()));
            }

            try
            {
                if (StartService(service, 0, null))
                {
                    return ServiceOutcome.Done("The updater is running.");
                }

                var error = Marshal.GetLastWin32Error();

                return error == ErrorServiceAlreadyRunning
                    ? ServiceOutcome.Done("The updater is already running.")
                    : ServiceOutcome.Failed(Explain("start the updater", error));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static ServiceOutcome Remove(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);

        if (manager == IntPtr.Zero)
        {
            return ServiceOutcome.Failed(Explain("open the service database", Marshal.GetLastWin32Error()));
        }

        try
        {
            var service = OpenService(manager, name, ServiceStop | ServiceQueryStatus | Delete);

            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();

                return error == ErrorServiceDoesNotExist
                    ? ServiceOutcome.Done("There was no updater to remove.")
                    : ServiceOutcome.Failed(Explain("open the updater", error));
            }

            try
            {
                Halt(service);

                return DeleteService(service)
                    ? ServiceOutcome.Done("The updater is stopped and removed.")
                    : ServiceOutcome.Failed(Explain("remove the updater", Marshal.GetLastWin32Error()));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static void Halt(IntPtr service)
    {
        var status = new ServiceStatus();

        if (!ControlService(service, ControlStop, ref status) && Marshal.GetLastWin32Error() == ErrorServiceNotActive)
        {
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (!QueryServiceStatus(service, out var current) || current.CurrentState == 1)
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static ServiceOutcome Reconfigure(IntPtr manager, string name, string description, string command)
    {
        var service = OpenService(manager, name, ServiceChangeConfig | ServiceQueryStatus | ServiceStart);

        if (service == IntPtr.Zero)
        {
            return ServiceOutcome.Failed(Explain("open the registered updater", Marshal.GetLastWin32Error()));
        }

        try
        {
            const uint noChange = 0xFFFFFFFF;

            ChangeServiceConfig(
                service,
                noChange,
                ServiceAutoStart,
                noChange,
                command,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                null);

            Describe(service, description);
            DelayStart(service);
            RestartOnFailure(service);

            return ServiceOutcome.Done("The registered updater was pointed at this build.");
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static void Describe(IntPtr service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        var block = Marshal.AllocHGlobal(IntPtr.Size);

        try
        {
            Marshal.WriteIntPtr(block, text);
            ChangeServiceConfig2(service, ConfigDescription, block);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(text);
        }
    }

    private static void DelayStart(IntPtr service)
    {
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceDelayedAutoStart>());

        try
        {
            Marshal.StructureToPtr(new ServiceDelayedAutoStart { Delayed = true }, block, fDeleteOld: false);
            ChangeServiceConfig2(service, ConfigDelayedAutoStart, block);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static void RestartOnFailure(IntPtr service)
    {
        var actions = new[]
        {
            new ServiceAction { Type = ActionRestart, Delay = 60_000 },
            new ServiceAction { Type = ActionRestart, Delay = 300_000 },
            new ServiceAction { Type = ActionRestart, Delay = 900_000 },
        };

        var actionSize = Marshal.SizeOf<ServiceAction>();
        var actionBlock = Marshal.AllocHGlobal(actionSize * actions.Length);
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActions>());

        try
        {
            for (var index = 0; index < actions.Length; index++)
            {
                Marshal.StructureToPtr(actions[index], actionBlock + (index * actionSize), fDeleteOld: false);
            }

            Marshal.StructureToPtr(
                new ServiceFailureActions
                {
                    ResetPeriod = 86_400,
                    RebootMessage = IntPtr.Zero,
                    Command = IntPtr.Zero,
                    Count = (uint)actions.Length,
                    Actions = actionBlock,
                },
                block,
                fDeleteOld: false);

            ChangeServiceConfig2(service, ConfigFailureActions, block);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(actionBlock);
        }
    }

    private static ServiceState Map(uint state) => state switch
    {
        1 => ServiceState.Stopped,
        2 => ServiceState.Starting,
        3 => ServiceState.Stopping,
        4 => ServiceState.Running,
        7 => ServiceState.Paused,
        _ => ServiceState.Unreadable,
    };

    private static string Explain(string what, int error) => error switch
    {
        ErrorAccessDenied => $"Windows would not let this process {what}. It needs administrator rights.",
        ErrorServiceDoesNotExist => "The updater is not registered on this machine.",
        _ => $"Windows could not {what}: {new Win32Exception(error).Message}",
    };

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStart
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool Delayed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceAction
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint Count;
        public IntPtr Actions;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateServiceW")]
    private static extern IntPtr CreateService(
        IntPtr manager,
        string name,
        string displayName,
        uint access,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? account,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPath,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? account,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint level, IntPtr info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint count, string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
