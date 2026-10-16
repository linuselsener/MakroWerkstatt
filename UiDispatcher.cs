using System.Windows.Threading;

namespace MakroWerkstatt;

public interface IUiDispatcher
{
    void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal);
    void Invoke(Action action);
    bool CheckAccess();
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _dispatcher.BeginInvoke(action, priority);
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.Invoke(action);
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();
}
