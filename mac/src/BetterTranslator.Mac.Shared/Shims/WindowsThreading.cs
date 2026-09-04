using System.Threading.Tasks;
using Avalonia.Threading;

namespace System.Windows.Threading;

public sealed class Dispatcher
{
    private readonly Avalonia.Threading.Dispatcher _inner;

    internal Dispatcher(Avalonia.Threading.Dispatcher inner) => _inner = inner;

    public bool CheckAccess() => _inner.CheckAccess();

    public void VerifyAccess() => _inner.VerifyAccess();

    public void Invoke(Action action) => _inner.Invoke(action);

    public T Invoke<T>(Func<T> action) => _inner.Invoke(action);

    public DispatcherOperation BeginInvoke(Action action) => new(_inner.InvokeAsync(action));

    public DispatcherOperation InvokeAsync(Action action) => new(_inner.InvokeAsync(action));

    public DispatcherOperation<T> InvokeAsync<T>(Func<T> action) => new(_inner.InvokeAsync(action));
}

public sealed class DispatcherOperation
{
    private readonly Avalonia.Threading.DispatcherOperation _inner;

    internal DispatcherOperation(Avalonia.Threading.DispatcherOperation inner) => _inner = inner;

    public Task Task => _inner.GetTask();
}

public sealed class DispatcherOperation<T>
{
    private readonly Avalonia.Threading.DispatcherOperation<T> _inner;

    internal DispatcherOperation(Avalonia.Threading.DispatcherOperation<T> inner) => _inner = inner;

    public Task<T> Task => _inner.GetTask();
}

public sealed class DispatcherTimer
{
    private readonly Avalonia.Threading.DispatcherTimer _inner = new();

    public DispatcherTimer() => _inner.Tick += (s, e) => Tick?.Invoke(this, EventArgs.Empty);

    public TimeSpan Interval
    {
        get => _inner.Interval;
        set => _inner.Interval = value;
    }

    public bool IsEnabled
    {
        get => _inner.IsEnabled;
        set => _inner.IsEnabled = value;
    }

    public event EventHandler? Tick;

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();
}
