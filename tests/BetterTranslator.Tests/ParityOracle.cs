using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Re-runs a module's oracle against the reference engine and compares it to the
/// checked-in fixture.
///
/// This is what stops the arrangement degrading into a port checked against a
/// snapshot nobody re-took: the fast tests compare against the fixture, and this
/// is the only thing that notices when the fixture and the reference have parted
/// company.
/// </summary>
internal static class ParityOracle
{
    public static void Regenerate(string module, string fixturePath, ITestOutputHelper output)
    {
        var script = Path.Combine(RepoRoot(), "tests", "parity", module, "oracle.ps1");

        File.Exists(script).Should().BeTrue($"the oracle generator must be at {script}");

        var regenerated = Path.Combine(Path.GetTempPath(), $"bt-parity-{module}-{Guid.NewGuid():N}.json");

        var psi = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Out", regenerated })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        output.WriteLine(stdout);

        // stderr is never failure on its own. That is the recorded gotcha from
        // the reference engine, where progress notes written to stderr alongside
        // exit 0 were being read as a dead model.
        if (stderr.Length > 0)
        {
            output.WriteLine("stderr (not a failure by itself): " + stderr);
        }

        process.ExitCode.Should().Be(0, "the oracle must run against the reference");

        try
        {
            File.ReadAllText(regenerated).Should().Be(
                File.ReadAllText(fixturePath),
                $"the reference has changed; regenerate tests\\parity\\{module}\\oracle.json and re-verify the port");
        }
        finally
        {
            File.Delete(regenerated);
        }
    }

    /// <summary>
    /// Up out of bin/Debug/&lt;tfm&gt;/ to the repository root. Five levels:
    /// tfm, Debug, bin, BetterTranslator.Tests, tests.
    /// </summary>
    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
