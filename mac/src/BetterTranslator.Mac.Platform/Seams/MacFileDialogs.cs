using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacFileDialogs(Func<TopLevel?> topLevel) : IFileDialogSeam, IFolderPickerSeam
{
    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool allowMultiple, IReadOnlyList<FileFilter> filters)
    {
        var storage = topLevel()?.StorageProvider;

        if (storage is null)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = Translate(filters),
        }).ConfigureAwait(true);

        return [.. files.Select(LocalPath).Where(p => p.Length > 0)];
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        var storage = topLevel()?.StorageProvider;

        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = Translate(filters),
        }).ConfigureAwait(true);

        var path = file is null ? string.Empty : LocalPath(file);

        return path.Length > 0 ? path : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var storage = topLevel()?.StorageProvider;

        if (storage is null)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        var folder = folders.FirstOrDefault();
        var path = folder is null ? string.Empty : LocalPath(folder);

        return path.Length > 0 ? path : null;
    }

    private static string LocalPath(IStorageItem item) => item.TryGetLocalPath() ?? string.Empty;

    private static List<FilePickerFileType>? Translate(IReadOnlyList<FileFilter> filters) =>
        filters.Count == 0
            ? null
            : [.. filters.Select(f => new FilePickerFileType(f.Name)
              {
                  Patterns = [.. f.Extensions.Select(e => "*." + e)],
              })];
}
