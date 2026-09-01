using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Media;
using System.Windows.Threading;

namespace System.Windows;

public sealed class Application
{
    private static readonly ConcurrentDictionary<object, System.Windows.Media.Brush> BrushCache = new();
    private static Application? _current;

    private Application(Avalonia.Application inner)
    {
        Inner = inner;
        Dispatcher = new Dispatcher(Avalonia.Threading.Dispatcher.UIThread);
    }

    public Avalonia.Application Inner { get; }

    public static Application? Current
    {
        get
        {
            var inner = Avalonia.Application.Current;

            if (inner is null)
            {
                _current = null;
                return null;
            }

            if (_current is null || !ReferenceEquals(_current.Inner, inner))
            {
                _current = new Application(inner);
            }

            return _current;
        }
    }

    public Dispatcher Dispatcher { get; }

    public IMainWindowSurface? MainWindow { get; set; }

    public Action<int>? ShutdownHandler { get; set; }

    public object FindResource(object key) =>
        TryFindResource(key) ?? throw new KeyNotFoundException($"Resource '{key}' was not found.");

    public object? TryFindResource(object key)
    {
        if (!Inner.TryFindResource(key, out var value) || value is null)
        {
            return null;
        }

        return Adapt(key, value);
    }

    public void Shutdown(int exitCode = 0) => ShutdownHandler?.Invoke(exitCode);

    private static object Adapt(object key, object value) => value switch
    {
        ISolidColorBrush brush => BrushCache.GetOrAdd(key, _ => new System.Windows.Media.SolidColorBrush(brush)),
        _ => value,
    };
}

public interface IMainWindowSurface
{
    object? DataContext { get; }

    WindowState WindowState { get; set; }

    void Activate();
}

public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
    FullScreen,
}
