using System.IO;
using System.Runtime.InteropServices;

namespace BetterTranslator.Core.Services.Restart;

public sealed record RelaunchTarget
{
    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string PayloadDirectory { get; init; }

    public required string Detail { get; init; }

    public bool IsResolved => Executable.Length > 0;

    public bool IsInsidePayload =>
        IsResolved
        && PayloadDirectory.Length > 0
        && Executable.StartsWith(PayloadDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public string CommandLine => Arguments.Count == 0
        ? Quote(Executable)
        : Quote(Executable) + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;
}

public static class ProcessRelaunch
{
    private const string ManagedExtension = ".dll";

    public static RelaunchTarget Resolve() => Resolve(
        Environment.ProcessPath,
        AppContext.BaseDirectory,
        ManagedEntryAssembly(),
        OuterArguments(),
        Environment.CurrentDirectory);

    public static RelaunchTarget Resolve(
        string? processPath,
        string payloadDirectory,
        string? managedEntryAssembly,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var payload = Normalise(payloadDirectory);

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return Unresolved(payload, "The operating system did not report a path for this process.");
        }

        var executable = Path.GetFullPath(processPath);

        if (managedEntryAssembly is { Length: > 0 }
            && string.Equals(executable, Path.GetFullPath(managedEntryAssembly), StringComparison.OrdinalIgnoreCase))
        {
            return Unresolved(payload, $"{executable} is the unpacked payload assembly rather than the shipped executable.");
        }

        if (executable.EndsWith(ManagedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Unresolved(payload, $"{executable} is a managed assembly rather than the shipped executable.");
        }

        var resolved = new RelaunchTarget
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory.Length > 0
                ? workingDirectory
                : Path.GetDirectoryName(executable) ?? string.Empty,
            PayloadDirectory = payload,
            Detail = string.Empty,
        };

        return resolved with
        {
            Detail = resolved.IsInsidePayload
                ? $"relaunch target {executable} sits inside the payload directory {payload}, which is a run with no bootstrap around it"
                : $"relaunch target {executable}, outside the payload directory {payload}",
        };
    }

    public static IReadOnlyList<string> OuterArguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [.. Environment.GetCommandLineArgs().Skip(1)];
        }

        var line = GetCommandLineW();

        if (line == IntPtr.Zero)
        {
            return [.. Environment.GetCommandLineArgs().Skip(1)];
        }

        var argv = CommandLineToArgvW(line, out var count);

        if (argv == IntPtr.Zero)
        {
            return [.. Environment.GetCommandLineArgs().Skip(1)];
        }

        try
        {
            var arguments = new List<string>(Math.Max(count - 1, 0));

            for (var index = 1; index < count; index++)
            {
                var entry = Marshal.ReadIntPtr(argv, index * IntPtr.Size);

                if (Marshal.PtrToStringUni(entry) is { Length: > 0 } value)
                {
                    arguments.Add(value);
                }
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string? ManagedEntryAssembly()
    {
        var argv = Environment.GetCommandLineArgs();

        return argv.Length > 0 ? argv[0] : null;
    }

    private static RelaunchTarget Unresolved(string payload, string detail) => new()
    {
        Executable = string.Empty,
        Arguments = [],
        WorkingDirectory = string.Empty,
        PayloadDirectory = payload,
        Detail = detail,
    };

    private static string Normalise(string folder) =>
        folder.Length == 0 ? string.Empty : Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(IntPtr commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
