using MakroWerkstatt;

namespace MakroWerkstatt.Tests;

internal sealed class FakeInputCaptureService : IInputCaptureService
{
    private bool _started;
    private bool _disposed;

    public event Action<MacroAction>? ActionCaptured;
    public event Action? EmergencyStopPressed;
    public event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;
    public event Action<InputCaptureStatus>? StatusChanged;

    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int FlushCount { get; private set; }
    public bool EmergencyStopEnabled { get; private set; }
    public bool HotkeysEnabled { get; private set; } = true;
    public IntPtr RawInputTarget { get; private set; }
    public (GlobalHotkey Recording, GlobalHotkey Playback)? Hotkeys { get; private set; }

    public void SetRawInputTarget(IntPtr handle) => RawInputTarget = handle;

    public void SetHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey) =>
        Hotkeys = (recordingHotkey, playbackHotkey);

    public void SetHotkeysEnabled(bool enabled) => HotkeysEnabled = enabled;

    public void SetEmergencyStopEnabled(bool enabled) => EmergencyStopEnabled = enabled;

    public void FlushPendingMouseMovement() => FlushCount++;

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        StatusChanged?.Invoke(new InputCaptureStatus(true, RelativeMouseCaptureAvailable: true));
    }

    public void Stop()
    {
        StopCount++;
        _started = false;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        DisposeCount++;
    }

    public void EmitAction(MacroAction action)
    {
        if (_started && !_disposed) ActionCaptured?.Invoke(action);
    }

    public void EmitEmergencyStop()
    {
        if (_started && !_disposed) EmergencyStopPressed?.Invoke();
    }

    public void EmitHotkey(GlobalHotkeyAction action, params int[] modifierKeys)
    {
        if (_started && !_disposed) HotkeyPressed?.Invoke(action, modifierKeys);
    }

    public void EmitActionRegardlessOfLifecycle(MacroAction action) => ActionCaptured?.Invoke(action);
}

internal sealed class FakeGlobalInputHook : IGlobalInputHook
{
    public event Action<MacroAction>? ActionCaptured;
    public event Action? EmergencyStopPressed;
    public event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;

    public bool EmergencyStopEnabled { get; set; }
    public bool HotkeysEnabled { get; set; } = true;
    public bool RelativeMouseCaptureAvailable { get; set; } = true;
    public bool ThrowOnConfigure { get; set; }
    public int DisposeCount { get; private set; }
    public int FlushCount { get; private set; }
    public (GlobalHotkey Recording, GlobalHotkey Playback)? Hotkeys { get; private set; }

    public void ConfigureHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey)
    {
        if (ThrowOnConfigure) throw new InvalidOperationException("Synthetic configuration failure.");
        Hotkeys = (recordingHotkey, playbackHotkey);
    }

    public void FlushPendingMouseMovement() => FlushCount++;

    public void Dispose() => DisposeCount++;

    public void EmitAction(MacroAction action) => ActionCaptured?.Invoke(action);

    public void EmitEmergencyStop() => EmergencyStopPressed?.Invoke();

    public void EmitHotkey(GlobalHotkeyAction action, params int[] modifierKeys) =>
        HotkeyPressed?.Invoke(action, modifierKeys);
}

internal sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<(Action Action, System.Windows.Threading.DispatcherPriority Priority)> _queue = new();

    public bool HasAccess { get; set; }
    public int PendingCount => _queue.Count;
    public List<System.Windows.Threading.DispatcherPriority> PostedPriorities { get; } = [];

    public void Post(Action action, System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Normal)
    {
        PostedPriorities.Add(priority);
        _queue.Enqueue((action, priority));
    }

    public void Invoke(Action action) => action();

    public bool CheckAccess() => HasAccess;

    public void Drain()
    {
        while (_queue.TryDequeue(out var item)) item.Action();
    }
}
