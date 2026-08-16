using System.Diagnostics;
using System.Text;

namespace BetterTranslator.Bench;

internal sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

internal static class Shell
{
    internal static ShellResult Capture(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory ?? Paths.RepoRoot,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { error.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? TimeSpan.FromMinutes(2);

        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ShellResult(-1, output.ToString(), error.ToString(), TimedOut: true);
        }

        process.WaitForExit();

        return new ShellResult(process.ExitCode, output.ToString(), error.ToString(), TimedOut: false);
    }

    internal static string Line(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var result = Capture(fileName, arguments, workingDirectory);
        var text = result.StandardOutput.Trim();

        return text.Length > 0 ? text.Split('\n')[0].Trim() : result.StandardError.Trim();
    }
}
