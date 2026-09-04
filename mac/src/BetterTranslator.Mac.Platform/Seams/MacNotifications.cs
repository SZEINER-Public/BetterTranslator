using System.Diagnostics;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacNotifications(IAppPathsSeam paths) : INotificationSeam
{
    private const string ScriptTool = "/usr/bin/osascript";

    public Task ShowAsync(string title, string body, string? actionId = null)
    {
        var script = $"display notification {Quote(body)} with title {Quote(title)}";

        var start = new ProcessStartInfo(ScriptTool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);

        try
        {
            using var started = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }

        return Task.CompletedTask;
    }

    public void WithdrawRegistration()
    {
        var marker = Path.Combine(paths.Root, "notifications.registered");

        try
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
