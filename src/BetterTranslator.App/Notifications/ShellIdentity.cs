using System.IO;
using System.Runtime.InteropServices;

namespace BetterTranslator.App.Notifications;

public static class ShellIdentity
{
    public const string AppUserModelId = "SZEINER.BetterTranslator";

    public const string ActivatorClsid = "6B1C2A94-3F5D-4E27-9A63-1D8E4C7B0F52";

    public const string ShortcutFileName = "BetterTranslator.lnk";

    public const string ActivatedVerb = "-ToastActivated";

    public static Guid Activator { get; } = new(ActivatorClsid);

    public static string ClsidKey => $@"Software\Classes\CLSID\{{{ActivatorClsid}}}";

    public static string LocalServerKey => ClsidKey + @"\LocalServer32";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        ShortcutFileName);

    public static string LocalServerCommand(string executable) => $"\"{executable}\" {ActivatedVerb}";

    public static bool Apply()
    {
        try
        {
            return SetCurrentProcessExplicitAppUserModelID(AppUserModelId) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string id);
}
