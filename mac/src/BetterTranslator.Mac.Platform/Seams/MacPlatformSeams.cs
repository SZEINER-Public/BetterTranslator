using Avalonia.Controls;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacPlatformSeams : IPlatformSeams, IDisposable
{
    public MacPlatformSeams(Func<TopLevel?> topLevel)
    {
        var dialogs = new MacFileDialogs(topLevel);
        var paths = new MacAppPaths();

        paths.EnsureCreated();

        MacPaths = paths;
        FileDialogs = dialogs;
        FolderPicker = dialogs;
        Clipboard = new MacClipboard(topLevel);
        Paths = paths;
        Notifications = new MacNotifications(paths);
        SingleInstance = new MacSingleInstance(paths);
        Shell = new MacShell();
        Updates = new MacUpdateTrigger();
        NativeRuntime = new MacNativeRuntime(paths);
        Backends = new MacInferenceBackends();
        UpdaterHost = new MacUpdaterHost(Updates);
    }

    public MacAppPaths MacPaths { get; }

    public IFileDialogSeam FileDialogs { get; }

    public IFolderPickerSeam FolderPicker { get; }

    public IClipboardSeam Clipboard { get; }

    public IAppPathsSeam Paths { get; }

    public INotificationSeam Notifications { get; }

    public ISingleInstanceSeam SingleInstance { get; }

    public IShellSeam Shell { get; }

    public IUpdateTriggerSeam Updates { get; }

    public INativeRuntimeSeam NativeRuntime { get; }

    public IInferenceBackendSeam Backends { get; }

    public MacUpdaterHost UpdaterHost { get; }

    public void Install()
    {
        BetterTranslator.Mac.Seams.Platform.Install(this);
        NativeRuntime.Install();
    }

    public void Dispose() => (SingleInstance as IDisposable)?.Dispose();
}
