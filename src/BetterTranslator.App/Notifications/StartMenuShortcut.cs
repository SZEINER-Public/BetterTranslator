using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterTranslator.App.Notifications;

public sealed class StartMenuShortcut : IShortcutStore
{
    private const int MaxPath = 260;
    private const ushort VarTypeLpwstr = 31;
    private const ushort VarTypeClsid = 72;

    private static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static readonly PropertyKey ToastActivatorKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 26);

    public string? TargetOf(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var link = (IShellLink)new ShellLink();
            ((IPersistFile)link).Load(path, 0);

            var target = new StringBuilder(MaxPath);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            var found = target.ToString();

            return found.Length == 0 ? null : found;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    public bool Create(string path, string target, string appUserModelId, Guid activator)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);

            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            var link = (IShellLink)new ShellLink();

            link.SetPath(target);
            link.SetArguments(string.Empty);

            if (Path.GetDirectoryName(target) is { Length: > 0 } workingFolder)
            {
                link.SetWorkingDirectory(workingFolder);
            }

            var properties = (IPropertyStore)link;

            Assign(properties, AppUserModelIdKey, appUserModelId);
            Assign(properties, ToastActivatorKey, activator);

            properties.Commit();

            ((IPersistFile)link).Save(path, true);

            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Remove(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Assign(IPropertyStore properties, PropertyKey key, string value)
    {
        var variant = new PropVariant { Type = VarTypeLpwstr, Value = Marshal.StringToCoTaskMemUni(value) };

        try
        {
            properties.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private static void Assign(IPropertyStore properties, PropertyKey key, Guid value)
    {
        var buffer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(value, buffer, fDeleteOld: false);

        var variant = new PropVariant { Type = VarTypeClsid, Value = buffer };

        try
        {
            properties.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr data, int flags);

        void GetIDList(out IntPtr list);

        void SetIDList(IntPtr list);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder folder, int length);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string folder);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int length, out int index);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);

        void Resolve(IntPtr window, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }
}
