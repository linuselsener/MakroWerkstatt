using System.Windows.Threading;
using MakroWerkstatt;

namespace MakroWerkstatt.Tests;

internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public List<DispatcherPriority> PostedPriorities { get; } = [];
    public int InvokeCount { get; private set; }
    public bool HasAccess { get; set; } = true;

    public void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        PostedPriorities.Add(priority);
        action();
    }

    public void Invoke(Action action)
    {
        InvokeCount++;
        action();
    }

    public bool CheckAccess() => HasAccess;
}
