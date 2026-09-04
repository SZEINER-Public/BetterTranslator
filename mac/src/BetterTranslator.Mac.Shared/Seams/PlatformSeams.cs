using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Models;

namespace BetterTranslator.Mac.Seams;

public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);

public interface IFileDialogSeam
{
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool allowMultiple, IReadOnlyList<FileFilter> filters);

    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters);
}

public interface IFolderPickerSeam
{
    Task<string?> PickFolderAsync(string title);
}

public interface IClipboardSeam
{
    Task SetTextAsync(string text);

    Task<string?> GetTextAsync();
}

public interface IAppPathsSeam
{
    string Root { get; }

    string Models { get; }

    string Runtime { get; }

    string Cache { get; }

    string Logs { get; }
}

public interface INotificationSeam
{
    Task ShowAsync(string title, string body, string? actionId = null);

    void WithdrawRegistration();
}

public interface ISingleInstanceSeam
{
    bool TryClaim(out string reason);

    event Action<IReadOnlyList<string>>? Activated;

    Task SignalExistingAsync(IReadOnlyList<string> arguments);
}

public interface IShellSeam
{
    Task OpenUriAsync(string uri);

    Task RevealAsync(string path);
}

public interface IUpdateTriggerSeam
{
    Task<bool> RequestAsync(CancellationToken cancellationToken);

    bool IsSupported { get; }

    string UnsupportedReason { get; }
}

public interface INativeRuntimeSeam
{
    void Install();

    string LibraryFileName { get; }

    IReadOnlyList<string> SearchPaths { get; }

    string? ResolvedPath { get; }
}

public interface IInferenceBackendSeam
{
    IReadOnlyList<RuntimeBackend> Available { get; }

    RuntimeBackend Selected { get; }

    string SelectedTitle { get; }

    string SelectedSummary { get; }
}

public interface IPlatformSeams
{
    IFileDialogSeam FileDialogs { get; }

    IFolderPickerSeam FolderPicker { get; }

    IClipboardSeam Clipboard { get; }

    IAppPathsSeam Paths { get; }

    INotificationSeam Notifications { get; }

    ISingleInstanceSeam SingleInstance { get; }

    IShellSeam Shell { get; }

    IUpdateTriggerSeam Updates { get; }

    INativeRuntimeSeam NativeRuntime { get; }

    IInferenceBackendSeam Backends { get; }
}

public static class Platform
{
    private static IPlatformSeams? _current;

    public static IPlatformSeams Current =>
        _current ?? throw new InvalidOperationException("Platform seams were not installed before first use.");

    public static bool IsInstalled => _current is not null;

    public static void Install(IPlatformSeams seams) => _current = seams;
}
