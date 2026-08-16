using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BetterTranslator.App.Services;

/// <summary>A rectangle in physical screen pixels, which is what Win32 deals in.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Keeps a maximized window's content on the screen.
///
/// Windows maximizes a resizable window to the work area inflated by the resize
/// frame, on the assumption that the frame is non-client and covers the
/// overhang. WindowChrome gives the client area the whole window rect, so the
/// overhang is content, drawn off the edge of the screen: measured at eight
/// pixels on every side against a 3440 by 1440 monitor. ScrollThumbWidth is
/// eight and the chat's bar sits at the window edge by design, so the bar was
/// not clipped, it was entirely outside the screen. The top eight went with it,
/// which is why the titlebar sat higher when maximized.
///
/// Answering WM_GETMINMAXINFO with the work area does not fix this, and was
/// tried first: WM_WINDOWPOSCHANGING carries the work area through unchanged
/// and WM_WINDOWPOSCHANGED then reports it inflated by the frame again. The
/// hook runs ahead of WPF's own in the HwndSource chain, which is added when
/// the source is created, so there is no getting behind the code that
/// re-inflates it. The window rect came out identical with the hook and
/// without it. So the frame is given back as an inset on the content instead.
///
/// The inset is measured rather than looked up. There is no system metric that
/// reports it: SystemParameters.WindowResizeBorderThickness is 4 here against a
/// measured overhang of 8, because it leaves out SM_CXPADDEDBORDER. Subtracting
/// the work area from the window rect is exact by construction, on any monitor,
/// at any scale factor, with the taskbar on any edge.
/// </summary>
public static class WindowFrame
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>
    /// How far the content has to come in from each edge of the window. Pure,
    /// and held apart from the measuring so the arithmetic is testable against
    /// the layouts that are awkward to reproduce: a scaled monitor, a taskbar
    /// on the top edge, a window that needs no inset at all.
    /// </summary>
    /// <param name="window">The window rect, in physical pixels.</param>
    /// <param name="work">The work area of its monitor, in physical pixels.</param>
    /// <param name="scaleX">Horizontal DPI scale, since a Thickness is in DIPs.</param>
    /// <param name="scaleY">Vertical DPI scale.</param>
    public static Thickness MaximizedInset(
        WindowState state,
        ScreenRect window,
        ScreenRect work,
        double scaleX,
        double scaleY)
    {
        if (state != WindowState.Maximized || scaleX <= 0 || scaleY <= 0)
        {
            return default;
        }

        // Clamped at zero: a window that does not reach an edge of the work
        // area needs nothing given back there, and a negative margin would
        // stretch the content outwards instead.
        return new Thickness(
            Math.Max(0, work.Left - window.Left) / scaleX,
            Math.Max(0, work.Top - window.Top) / scaleY,
            Math.Max(0, window.Right - work.Right) / scaleX,
            Math.Max(0, window.Bottom - work.Bottom) / scaleY);
    }

    /// <summary>The inset for a window as it stands right now.</summary>
    public static Thickness MaximizedInset(Window window)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            return default;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
        {
            return default;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return default;
        }

        var dpi = VisualTreeHelper.GetDpi(window);

        return MaximizedInset(
            window.WindowState,
            new ScreenRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            dpi.DpiScaleX,
            dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
