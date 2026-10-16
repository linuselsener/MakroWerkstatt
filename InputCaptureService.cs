using System.Windows.Threading;

namespace MakroWerkstatt;

public sealed record InputCaptureStatus(bool IsAvailable, bool RelativeMouseCaptureAvailable);

public interface IGlobalInputHook : IDisposable
{
    event Action<MacroAction>? ActionCaptured;
    event Action? EmergencyStopPressed;
    event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;
    bool EmergencyStopEnabled { get; set; }
    bool HotkeysEnabled { get; set; }
    bool RelativeMouseCaptureAvailable { get; }
    void ConfigureHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey);
    void FlushPendingMouseMovement();
}

public interface IInputCaptureService : IDisposable
{
    event Action<MacroAction>? ActionCaptured;
    event Action? EmergencyStopPressed;
    event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;
    event Action<InputCaptureStatus>? StatusChanged;

    void SetRawInputTarget(IntPtr handle);
    void SetHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey);
    void SetHotkeysEnabled(bool enabled);
    void SetEmergencyStopEnabled(bool enabled);
    void FlushPendingMouseMovement();
    void Start();
    void Stop();
}

/// <summary>Owns the global input hook and delivers its events to the UI thread.</summary>
public sealed class GlobalInputCaptureService : IInputCaptureService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IntPtr, IGlobalInputHook> _hookFactory;
    private readonly object _lifecycleLock = new();
    private IGlobalInputHook? _hook;
    private IntPtr _rawInputTarget;
    private GlobalHotkey _recordingHotkey = new(0x78, HotkeyModifiers.None);
    private GlobalHotkey _playbackHotkey = new(0x79, HotkeyModifiers.None);
    private int _started;
    private int _disposed;
    private bool _hotkeysEnabled = true;
    private bool _emergencyStopEnabled;

    public event Action<MacroAction>? ActionCaptured;
    public event Action? EmergencyStopPressed;
    public event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;
    public event Action<InputCaptureStatus>? StatusChanged;

    public GlobalInputCaptureService(IUiDispatcher dispatcher, Func<IntPtr, IGlobalInputHook>? hookFactory = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hookFactory = hookFactory ?? (handle => new GlobalInputHook(handle));
    }

    public void SetRawInputTarget(IntPtr handle)
    {
        lock (_lifecycleLock)
        {
            if (_started != 0 || _disposed != 0)
                throw new InvalidOperationException("Das Raw-Input-Ziel muss vor dem Start gesetzt werden.");
            _rawInputTarget = handle;
        }
    }

    public void SetHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey)
    {
        ArgumentNullException.ThrowIfNull(recordingHotkey);
        ArgumentNullException.ThrowIfNull(playbackHotkey);
        if (recordingHotkey == playbackHotkey)
            throw new ArgumentException("Aufnahme- und Wiedergabe-Kürzel müssen verschieden sein.");

        lock (_lifecycleLock)
        {
            if (_disposed != 0) return;
            _recordingHotkey = recordingHotkey;
            _playbackHotkey = playbackHotkey;
            _hook?.ConfigureHotkeys(recordingHotkey, playbackHotkey);
        }
    }

    public void SetHotkeysEnabled(bool enabled)
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0) return;
            _hotkeysEnabled = enabled;
            if (_hook is not null) _hook.HotkeysEnabled = enabled;
        }
    }

    public void SetEmergencyStopEnabled(bool enabled)
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0) return;
            _emergencyStopEnabled = enabled;
            if (_hook is not null) _hook.EmergencyStopEnabled = enabled;
        }
    }

    public void FlushPendingMouseMovement()
    {
        lock (_lifecycleLock)
        {
            if (_disposed == 0) _hook?.FlushPendingMouseMovement();
        }
    }

    public void Start()
    {
        bool relativeMouseAvailable;
        lock (_lifecycleLock)
        {
            if (_disposed != 0 || _started != 0) return;

            var hook = _hookFactory(_rawInputTarget);
            try
            {
                hook.ActionCaptured += OnActionCaptured;
                hook.EmergencyStopPressed += OnEmergencyStopPressed;
                hook.HotkeyPressed += OnHotkeyPressed;
                hook.ConfigureHotkeys(_recordingHotkey, _playbackHotkey);
                hook.HotkeysEnabled = _hotkeysEnabled;
                hook.EmergencyStopEnabled = _emergencyStopEnabled;
                _hook = hook;
                Volatile.Write(ref _started, 1);
                relativeMouseAvailable = hook.RelativeMouseCaptureAvailable;
            }
            catch
            {
                hook.ActionCaptured -= OnActionCaptured;
                hook.EmergencyStopPressed -= OnEmergencyStopPressed;
                hook.HotkeyPressed -= OnHotkeyPressed;
                try { hook.Dispose(); }
                catch (Exception cleanupError)
                {
                    FileLogger.Error("GlobalInputCaptureService.Start", "Input hook startup cleanup failed", cleanupError);
                }
                throw;
            }
        }

        if (Volatile.Read(ref _started) != 0)
            PublishStatus(new InputCaptureStatus(true, relativeMouseAvailable));
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        IGlobalInputHook? hook;
        lock (_lifecycleLock)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            Volatile.Write(ref _started, 0);
            hook = _hook;
            _hook = null;
        }

        if (hook is null) return;
        hook.ActionCaptured -= OnActionCaptured;
        hook.EmergencyStopPressed -= OnEmergencyStopPressed;
        hook.HotkeyPressed -= OnHotkeyPressed;
        try { hook.Dispose(); }
        catch (Exception ex)
        {
            FileLogger.Error("GlobalInputCaptureService.Dispose", "Input hook cleanup failed", ex);
        }
    }

    private void OnActionCaptured(MacroAction action)
    {
        if (Volatile.Read(ref _started) == 0) return;
        if (_dispatcher.CheckAccess())
        {
            if (Volatile.Read(ref _started) != 0) ActionCaptured?.Invoke(action);
            return;
        }

        Post(() =>
        {
            if (Volatile.Read(ref _started) != 0) ActionCaptured?.Invoke(action);
        }, DispatcherPriority.Input);
    }

    private void OnEmergencyStopPressed()
    {
        if (Volatile.Read(ref _started) == 0) return;
        Post(() =>
        {
            if (Volatile.Read(ref _started) != 0) EmergencyStopPressed?.Invoke();
        }, DispatcherPriority.Send);
    }

    private void OnHotkeyPressed(GlobalHotkeyAction action, int[] modifierKeys)
    {
        if (Volatile.Read(ref _started) == 0) return;
        Post(() =>
        {
            if (Volatile.Read(ref _started) != 0) HotkeyPressed?.Invoke(action, modifierKeys);
        }, DispatcherPriority.Send);
    }

    private void PublishStatus(InputCaptureStatus status)
    {
        if (_dispatcher.CheckAccess())
        {
            StatusChanged?.Invoke(status);
            return;
        }
        Post(() => StatusChanged?.Invoke(status), DispatcherPriority.Normal);
    }

    private void Post(Action action, DispatcherPriority priority)
    {
        try { _dispatcher.Post(action, priority); }
        catch (Exception ex)
        {
            FileLogger.Warning("GlobalInputCaptureService.Dispatch", "Input event dispatch failed", ex);
        }
    }
}

internal sealed class InertInputCaptureService : IInputCaptureService
{
    public event Action<MacroAction>? ActionCaptured { add { } remove { } }
    public event Action? EmergencyStopPressed { add { } remove { } }
    public event Action<GlobalHotkeyAction, int[]>? HotkeyPressed { add { } remove { } }
    public event Action<InputCaptureStatus>? StatusChanged { add { } remove { } }

    public void SetRawInputTarget(IntPtr handle) { }
    public void SetHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey) { }
    public void SetHotkeysEnabled(bool enabled) { }
    public void SetEmergencyStopEnabled(bool enabled) { }
    public void FlushPendingMouseMovement() { }
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
