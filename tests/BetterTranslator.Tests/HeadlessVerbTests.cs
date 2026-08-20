using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class HeadlessVerbFactAttribute : FactAttribute
{
    public HeadlessVerbFactAttribute()
    {
        if (HeadlessVerbTests.Executable is null)
        {
            Skip = "Build BetterTranslator.App to run the headless verb checks.";
        }
    }
}

/// <summary>
/// Every step of the update path runs the application again with a verb and
/// reads the exit code back: registering the service, removing it, handing over
/// to a staged build and raising a notification. All four go through
/// <see cref="App.OnStartup"/>, which no other test touches, so a fault there is
/// invisible until someone turns automatic updates on and gets told the
/// elevated step did not finish. This runs the built executable the way the
/// updater runs it.
/// </summary>
public sealed class HeadlessVerbTests
{
    internal static string? Executable { get; } = Locate();

    [HeadlessVerbFact]
    public void AVerbReturnsItsOwnExitCodeRatherThanCrashing()
    {
        // --finish-update wants a process id and an installed path after it.
        // With neither it refuses and returns 2 before touching a file, a
        // registry key or a service, so this asks one thing only: was the verb
        // reached. A crash in OnStartup surfaces as 0xE0434352 instead.
        (int exitCode, string output) = Run("--finish-update");

        exitCode.Should().Be(
            2,
            "the handler refuses a short argument list with 2, and an unhandled exception in startup "
            + $"would surface as -532462766 instead. The run said: {output}");
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"{Executable} did not start");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }

    /// <summary>
    /// The application is a sibling project rather than a reference this test
    /// can resolve a path from, so it is found from the repository root. The
    /// shallowest match wins, which is the plain build output rather than a
    /// runtime identifier or publish folder beneath it.
    /// </summary>
    private static string? Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetterTranslator.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        string configuration = AppContext.BaseDirectory.Contains(
            Path.Combine("bin", "Release"),
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string built = Path.Combine(directory.FullName, "src", "BetterTranslator.App", "bin", configuration);

        return Directory.Exists(built)
            ? Directory.EnumerateFiles(built, "BetterTranslator.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }
}
