using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BetterTranslator.Updates.Service;

public sealed record SessionLaunch(bool Started, string Detail)
{
    public static SessionLaunch Refused(string detail) => new(false, detail);
}

[SupportedOSPlatform("windows")]
public static class InteractiveSession
{
    private const uint NoSession = 0xFFFFFFFF;

    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    private const uint StartFUseShowWindow = 0x00000001;
    private const short ShowHide = 0;

    public static bool HasActiveUser() => WTSGetActiveConsoleSessionId() != NoSession;

    public static SessionLaunch Launch(string executable, string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SessionLaunch.Refused("Only Windows has an interactive session to launch into.");
        }

        if (!File.Exists(executable))
        {
            return SessionLaunch.Refused("The application was not found where it was recorded.");
        }

        var session = WTSGetActiveConsoleSessionId();

        if (session == NoSession)
        {
            return SessionLaunch.Refused("No one is signed in at the console.");
        }

        if (!WTSQueryUserToken(session, out var userToken))
        {
            return SessionLaunch.Refused(Explain("read the signed-in user token", Marshal.GetLastWin32Error()));
        }

        var primary = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TokenAllAccess,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primary))
            {
                return SessionLaunch.Refused(Explain("duplicate the user token", Marshal.GetLastWin32Error()));
            }

            if (!CreateEnvironmentBlock(out environment, primary, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default",
                Flags = StartFUseShowWindow,
                ShowWindow = ShowHide,
            };

            var commandLine = new StringBuilder("\"" + executable + "\"");

            if (arguments.Length > 0)
            {
                commandLine.Append(' ').Append(arguments);
            }

            var flags = CreateUnicodeEnvironment | CreateNoWindow;
            var folder = Path.GetDirectoryName(executable);

            if (!CreateProcessAsUser(
                    primary,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    flags,
                    environment,
                    folder,
                    ref startup,
                    out var created))
            {
                return SessionLaunch.Refused(Explain("start the notifier in the user session", Marshal.GetLastWin32Error()));
            }

            CloseHandle(created.Process);
            CloseHandle(created.Thread);

            return new SessionLaunch(true, "The notifier was started in the signed-in session.");
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primary != IntPtr.Zero)
            {
                CloseHandle(primary);
            }

            CloseHandle(userToken);
        }
    }

    public static int ActiveSessionId()
    {
        var session = WTSGetActiveConsoleSessionId();

        return session == NoSession ? -1 : (int)session;
    }

    private static string Explain(string what, int error) =>
        $"Windows would not {what}: {new Win32Exception(error).Message}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint session, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existing,
        uint access,
        IntPtr attributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr duplicated);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
