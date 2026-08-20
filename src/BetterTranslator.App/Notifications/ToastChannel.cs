using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using BetterTranslator.Updates.Notifications;
using Windows.UI.Notifications;

namespace BetterTranslator.App.Notifications;

public interface INotificationChannel
{
    bool TryShow(PendingUpdateNotice notice, string installedVersion, out string detail);

    void Withdraw();
}

public sealed class ToastChannel(string appUserModelId) : INotificationChannel
{
    public bool TryShow(PendingUpdateNotice notice, string installedVersion, out string detail)
    {
        try
        {
            var document = new Windows.Data.Xml.Dom.XmlDocument();
            document.LoadXml(ToastContent.Build(notice, installedVersion, AppIconFile.Path()));

            var toast = new ToastNotification(document)
            {
                Tag = ToastContent.Tag,
                Group = ToastContent.Group,
            };

            ToastNotificationManager.CreateToastNotifier(appUserModelId).Show(toast);

            detail = "The notification was raised.";

            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or TypeLoadException or DllNotFoundException)
        {
            detail = "Windows would not raise the notification: " + ex.Message;

            return false;
        }
    }

    public void Withdraw()
    {
        try
        {
            ToastNotificationManager.History.Remove(ToastContent.Tag, ToastContent.Group, appUserModelId);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or TypeLoadException or DllNotFoundException)
        {
        }
    }
}

public static class AppIconFile
{
    private const string ResourceUri = "pack://application:,,,/BetterTranslator;component/Assets/app-icon.ico";

    public static string File => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterTranslator",
        "updates",
        "app-icon.png");

    public static string? Path()
    {
        var target = File;

        if (System.IO.File.Exists(target))
        {
            return target;
        }

        try
        {
            var source = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute));

            if (source is null)
            {
                return null;
            }

            using var stream = source.Stream;

            var decoder = new IconBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();

            if (frame is null)
            {
                return null;
            }

            var folder = System.IO.Path.GetDirectoryName(target);

            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));

            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(output);

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
