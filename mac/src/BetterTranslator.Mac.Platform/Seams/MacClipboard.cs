using Avalonia.Controls;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacClipboard(Func<TopLevel?> topLevel) : IClipboardSeam
{
    public async Task SetTextAsync(string text)
    {
        var clipboard = topLevel()?.Clipboard;

        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public async Task<string?> GetTextAsync()
    {
        var clipboard = topLevel()?.Clipboard;

        return clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(true);
    }
}
