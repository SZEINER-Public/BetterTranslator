using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using BetterTranslator.Mac.Seams;

namespace Microsoft.Win32;

public abstract class FileDialog
{
    public string Title { get; set; } = string.Empty;

    public string Filter { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public bool AddExtension { get; set; } = true;

    public string[] FileNames { get; protected set; } = [];

    public abstract bool? ShowDialog();

    protected IReadOnlyList<FileFilter> ParsedFilters()
    {
        if (string.IsNullOrWhiteSpace(Filter))
        {
            return [];
        }

        var parts = Filter.Split('|');
        var filters = new List<FileFilter>();

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var extensions = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().TrimStart('*', '.'))
                .Where(p => p.Length > 0)
                .ToList();

            filters.Add(new FileFilter(parts[i].Trim(), extensions));
        }

        return filters;
    }

    internal static T RunBlockingPublic<T>(Func<Task<T>> work) => RunBlocking(work);

    protected static T RunBlocking<T>(Func<Task<T>> work)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return work().GetAwaiter().GetResult();
        }

        var frame = new DispatcherFrame();
        var result = default(T)!;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                result = await work().ConfigureAwait(true);
            }
            finally
            {
                frame.Continue = false;
            }
        });

        Dispatcher.UIThread.PushFrame(frame);

        return result;
    }
}

public sealed class OpenFileDialog : FileDialog
{
    public bool Multiselect { get; set; }

    public override bool? ShowDialog()
    {
        var chosen = RunBlocking(() =>
            Platform.Current.FileDialogs.OpenFilesAsync(Title, Multiselect, ParsedFilters()));

        FileNames = [.. chosen];
        FileName = FileNames.FirstOrDefault() ?? string.Empty;

        return FileNames.Length > 0;
    }
}

public sealed class SaveFileDialog : FileDialog
{
    public override bool? ShowDialog()
    {
        var chosen = RunBlocking(() =>
            Platform.Current.FileDialogs.SaveFileAsync(Title, FileName, ParsedFilters()));

        if (chosen is null)
        {
            return false;
        }

        FileName = chosen;
        FileNames = [chosen];

        return true;
    }
}

public sealed class OpenFolderDialog
{
    public string Title { get; set; } = string.Empty;

    public bool Multiselect { get; set; }

    public string FolderName { get; set; } = string.Empty;

    public string[] FolderNames { get; private set; } = [];

    public bool? ShowDialog()
    {
        var chosen = FileDialog.RunBlockingPublic(() => Platform.Current.FolderPicker.PickFolderAsync(Title));

        if (chosen is null)
        {
            return false;
        }

        FolderName = chosen;
        FolderNames = [chosen];

        return true;
    }
}
