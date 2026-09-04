using BetterTranslator.Mac.Seams;

namespace System.Windows;

public static class Clipboard
{
    public static void SetText(string text)
    {
        if (Platform.IsInstalled)
        {
            _ = Platform.Current.Clipboard.SetTextAsync(text);
        }
    }

    public static string GetText() =>
        Platform.IsInstalled ? Platform.Current.Clipboard.GetTextAsync().GetAwaiter().GetResult() ?? string.Empty : string.Empty;
}
