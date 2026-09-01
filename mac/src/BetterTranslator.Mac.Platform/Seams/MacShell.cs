using System.Diagnostics;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacShell : IShellSeam
{
    private const string OpenTool = "/usr/bin/open";

    public Task OpenUriAsync(string uri) => RunAsync([uri]);

    public Task RevealAsync(string path) => RunAsync(["-R", path]);

    private static Task RunAsync(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(OpenTool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var started = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }

        return Task.CompletedTask;
    }
}
