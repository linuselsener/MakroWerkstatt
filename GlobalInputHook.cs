using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MakroWerkstatt;

/// <summary>Listens for system-wide mouse and keyboard input while the user explicitly records.</summary>
public sealed class GlobalInputHook : IGlobalInputHook
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmInput = 0x00FF;
    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmRightDown = 0x0204;
    private const int WmRightUp = 0x0205;
    private const int WmMiddleDown = 0x0207;
    private const int WmMiddleUp = 0x0208;
    private const int WmWheel = 0x020A;
    private const int WmXDown = 0x020B;
    private const int WmXUp = 0x020C;
    private const int VkEscape = 0x1B;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfUp = 0x80;
    private const uint LlMhfInjected = 0x01;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const long RawMoveCaptureIntervalMs = 8;

    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private readonly HwndSource? _windowSource;
    private readonly HwndSourceHook? _rawInputWindowHook;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _shortcutKeyUpsToSwallow = [];
    private readonly HashSet<int> _modifierKeyUpsToSkipCapture = [];
    private readonly object _hotkeyLock = new();
    private GlobalHotkey? _recordingHotkey;
    private GlobalHotkey? _playbackHotkey;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private long _lastMoveTick;
    private int _lastMoveX = int.MinValue;
    private int _lastMoveY = int.MinValue;
    private long _rawMoveStartTick;
    private int _pendingRawMoveX;
    private int _pendingRawMoveY;
    private bool _rawInputWindowHookAttached;
    private int _disposedFlag;
    private volatile bool _emergencyStopEnabled;
    private volatile bool _hotkeysEnabled = true;

    public event Action<MacroAction>? ActionCaptured;
    public event Action? EmergencyStopPressed;
    public event Action<GlobalHotkeyAction, int[]>? HotkeyPressed;
    public bool EmergencyStopEnabled { get => _emergencyStopEnabled; set => _emergencyStopEnabled = value; }
    public bool HotkeysEnabled { get => _hotkeysEnabled; set => _hotkeysEnabled = value; }
    public bool RelativeMouseCaptureAvailable { get; }

    public void ConfigureHotkeys(GlobalHotkey recordingHotkey, GlobalHotkey playbackHotkey)
    {
        if (recordingHotkey == playbackHotkey)
            throw new ArgumentException("Aufnahme- und Wiedergabe-Kürzel müssen verschieden sein.");
        lock (_hotkeyLock)
        {
            _recordingHotkey = recordingHotkey;
            _playbackHotkey = playbackHotkey;
        }
    }

    public GlobalInputHook(IntPtr rawInputTarget = default)
    {
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;
        var moduleHandle = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            FileLogger.Error("GlobalInputHook.SetWindowsHookEx", "KeyboardHook installation failed", win32ErrorCode: error);
            throw new Win32Exception(error, "Der globale Tastatur-Hook konnte nicht eingerichtet werden.");
        }

        try { _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, moduleHandle, 0); }
        catch
        {
            Dispose();
            throw;
        }
        if (_mouseHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            FileLogger.Error("GlobalInputHook.SetWindowsHookEx", "MouseHook installation failed", win32ErrorCode: error);
            Unhook(_keyboardHook, "KeyboardHook cleanup failed");
            _keyboardHook = IntPtr.Zero;
            throw new Win32Exception(error, "Der globale Maus-Hook konnte nicht eingerichtet werden.");
        }

        try
        {
            if (rawInputTarget != IntPtr.Zero && HwndSource.FromHwnd(rawInputTarget) is { } source)
            {
                _windowSource = source;
                _rawInputWindowHook = RawInputWindowProc;
                source.AddHook(_rawInputWindowHook);
                _rawInputWindowHookAttached = true;
                var device = new RawInputDevice
                {
                    UsagePage = 0x01,
                    Usage = 0x02,
                    Flags = RidevInputSink,
                    Target = rawInputTarget
                };
                RelativeMouseCaptureAvailable = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
                if (!RelativeMouseCaptureAvailable)
                {
                    FileLogger.Warning("GlobalInputHook.RegisterRawInputDevices", "RawInput registration failed",
                        win32ErrorCode: Marshal.GetLastWin32Error());
                    source.RemoveHook(_rawInputWindowHook);
                    _rawInputWindowHookAttached = false;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IntPtr RawInputWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput) return IntPtr.Zero;
        try { ReadRawMouseInput(lParam); }
        catch (Exception ex)
        {
            FileLogger.Error("GlobalInputHook.RawInputWindowProc", "RawInput window callback failed", ex);
        }
        handled = true;
        try { return DefWindowProc(hwnd, message, wParam, lParam); }
        catch (Exception ex)
        {
            FileLogger.Error("GlobalInputHook.DefWindowProc", "RawInput default window procedure failed", ex);
            return IntPtr.Zero;
        }
    }

    private void ReadRawMouseInput(IntPtr rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        var queryResult = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (queryResult != 0)
        {
            FileLogger.Warning("GlobalInputHook.GetRawInputData", "RawInput size query failed",
                win32ErrorCode: Marshal.GetLastWin32Error());
            return;
        }
        if (size < headerSize + (uint)Marshal.SizeOf<RawMouseData>())
            return;

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var read = GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize);
            if (read == uint.MaxValue)
            {
                FileLogger.Warning("GlobalInputHook.GetRawInputData", "RawInput data read failed",
                    win32ErrorCode: Marshal.GetLastWin32Error());
                return;
            }
            if (read < headerSize + (uint)Marshal.SizeOf<RawMouseData>()) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeMouse) return;
            var mouse = Marshal.PtrToStructure<RawMouseData>(IntPtr.Add(buffer, checked((int)headerSize)));
            if ((mouse.Flags & MouseMoveAbsolute) != 0 || (mouse.LastX == 0 && mouse.LastY == 0)) return;
            BufferRawMouseMovement(mouse.LastX, mouse.LastY);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void BufferRawMouseMovement(int dx, int dy)
    {
        var now = Environment.TickCount64;
        if ((_pendingRawMoveX != 0 || _pendingRawMoveY != 0) && now - _rawMoveStartTick >= RawMoveCaptureIntervalMs)
            FlushPendingMouseMovement();

        if (_pendingRawMoveX == 0 && _pendingRawMoveY == 0)
            _rawMoveStartTick = now;
        _pendingRawMoveX = unchecked(_pendingRawMoveX + dx);
        _pendingRawMoveY = unchecked(_pendingRawMoveY + dy);
        if (now - _rawMoveStartTick >= RawMoveCaptureIntervalMs)
            FlushPendingMouseMovement();
    }

    public void FlushPendingMouseMovement()
    {
        var dx = _pendingRawMoveX;
        var dy = _pendingRawMoveY;
        _pendingRawMoveX = 0;
        _pendingRawMoveY = 0;
        if (dx == 0 && dy == 0) return;
        ActionCaptured?.Invoke(new MacroAction { Type = MacroActionType.MouseMoveRelative, X = dx, Y = dy });
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = unchecked((int)wParam.ToInt64());
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if ((data.Flags & LlkhfInjected) == 0)
                {
                    var isDown = message is WmKeyDown or WmSysKeyDown;
                    var isUp = message is WmKeyUp or WmSysKeyUp;
                    var keyCode = unchecked((int)data.VirtualKey);

                    if (isDown)
                    {
                        FlushPendingMouseMovement();
                        var isRepeat = !_pressedKeys.Add(keyCode);
                        if (EmergencyStopEnabled && keyCode == VkEscape)
                        {
                            if (!isRepeat) EmergencyStopPressed?.Invoke();
                            _shortcutKeyUpsToSwallow.Add(keyCode);
                            return new IntPtr(1);
                        }

                        if (TryMatchHotkey(keyCode, out var hotkeyAction, out var modifiers))
                        {
                            if (!isRepeat)
                            {
                                foreach (var modifierKey in modifiers)
                                    _modifierKeyUpsToSkipCapture.Add(modifierKey);
                                HotkeyPressed?.Invoke(hotkeyAction, modifiers);
                            }
                            _shortcutKeyUpsToSwallow.Add(keyCode);
                            return new IntPtr(1);
                        }

                        ActionCaptured?.Invoke(new MacroAction
                        {
                            Type = MacroActionType.KeyDown,
                            KeyCode = keyCode,
                            ScanCode = unchecked((int)data.ScanCode),
                            RawFlags = unchecked((int)data.Flags)
                        });
                    }
                    else if (isUp)
                    {
                        FlushPendingMouseMovement();
                        _pressedKeys.Remove(keyCode);
                        if (_shortcutKeyUpsToSwallow.Remove(keyCode)) return new IntPtr(1);
                        if (_modifierKeyUpsToSkipCapture.Remove(keyCode))
                            return CallNextHookEx(_keyboardHook, code, wParam, lParam);

                        ActionCaptured?.Invoke(new MacroAction
                        {
                            Type = MacroActionType.KeyUp,
                            KeyCode = keyCode,
                            ScanCode = unchecked((int)data.ScanCode),
                            RawFlags = unchecked((int)data.Flags)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Exceptions must never cross a native hook callback boundary.
                FileLogger.Error("GlobalInputHook.KeyboardHookCallback", "KeyboardHook callback failed", ex);
            }
        }
        try { return CallNextHookEx(_keyboardHook, code, wParam, lParam); }
        catch (Exception ex)
        {
            FileLogger.Error("GlobalInputHook.CallNextHookEx", "KeyboardHook continuation failed", ex);
            return IntPtr.Zero;
        }
    }

    private bool TryMatchHotkey(int keyCode, out GlobalHotkeyAction action, out int[] modifierKeys)
    {
        action = default;
        modifierKeys = [];
        if (!HotkeysEnabled) return false;

        var modifiers = GetPressedModifiers();
        lock (_hotkeyLock)
        {
            if (_recordingHotkey is { } record && record.VirtualKey == keyCode && record.Modifiers == modifiers)
            {
                action = GlobalHotkeyAction.ToggleRecording;
                modifierKeys = GetPressedModifierKeys();
                return true;
            }
            if (_playbackHotkey is { } playback && playback.VirtualKey == keyCode && playback.Modifiers == modifiers)
            {
                action = GlobalHotkeyAction.TogglePlayback;
                modifierKeys = GetPressedModifierKeys();
                return true;
            }
        }
        return false;
    }

    private HotkeyModifiers GetPressedModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (_pressedKeys.Any(IsControlKey)) modifiers |= HotkeyModifiers.Control;
        if (_pressedKeys.Any(IsAltKey)) modifiers |= HotkeyModifiers.Alt;
        if (_pressedKeys.Any(IsShiftKey)) modifiers |= HotkeyModifiers.Shift;
        return modifiers;
    }

    private int[] GetPressedModifierKeys() => _pressedKeys.Where(IsModifierKey).ToArray();

    private static bool IsControlKey(int key) => key is 0x11 or 0xA2 or 0xA3;
    private static bool IsAltKey(int key) => key is 0x12 or 0xA4 or 0xA5;
    private static bool IsShiftKey(int key) => key is 0x10 or 0xA0 or 0xA1;
    private static bool IsModifierKey(int key) => IsControlKey(key) || IsAltKey(key) || IsShiftKey(key);

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = unchecked((int)wParam.ToInt64());
                var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                if ((data.Flags & LlMhfInjected) == 0)
                {
                    var x = data.Point.X;
                    var y = data.Point.Y;
                    switch (message)
                    {
                        case WmMouseMove:
                        {
                            var now = Environment.TickCount64;
                            var movedEnough = _lastMoveX == int.MinValue || Math.Abs(x - _lastMoveX) + Math.Abs(y - _lastMoveY) >= 2;
                            if (movedEnough && now - _lastMoveTick >= 12)
                            {
                                _lastMoveTick = now;
                                _lastMoveX = x;
                                _lastMoveY = y;
                                ActionCaptured?.Invoke(new MacroAction { Type = MacroActionType.MouseMove, X = x, Y = y });
                            }
                            break;
                        }
                        case WmLeftDown: FlushPendingMouseMovement(); CaptureButton("Left", true, x, y); break;
                        case WmLeftUp: FlushPendingMouseMovement(); CaptureButton("Left", false, x, y); break;
                        case WmRightDown: FlushPendingMouseMovement(); CaptureButton("Right", true, x, y); break;
                        case WmRightUp: FlushPendingMouseMovement(); CaptureButton("Right", false, x, y); break;
                        case WmMiddleDown: FlushPendingMouseMovement(); CaptureButton("Middle", true, x, y); break;
                        case WmMiddleUp: FlushPendingMouseMovement(); CaptureButton("Middle", false, x, y); break;
                        case WmXDown: FlushPendingMouseMovement(); CaptureButton(HighWord(data.MouseData) == 1 ? "X1" : "X2", true, x, y); break;
                        case WmXUp: FlushPendingMouseMovement(); CaptureButton(HighWord(data.MouseData) == 1 ? "X1" : "X2", false, x, y); break;
                        case WmWheel:
                            FlushPendingMouseMovement();
                            ActionCaptured?.Invoke(new MacroAction
                            {
                                Type = MacroActionType.MouseWheel,
                                X = x,
                                Y = y,
                                WheelDelta = unchecked((short)HighWord(data.MouseData))
                            });
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Exceptions must never cross a native hook callback boundary.
                FileLogger.Error("GlobalInputHook.MouseHookCallback", "MouseHook callback failed", ex);
            }
        }
        try { return CallNextHookEx(_mouseHook, code, wParam, lParam); }
        catch (Exception ex)
        {
            FileLogger.Error("GlobalInputHook.CallNextHookEx", "MouseHook continuation failed", ex);
            return IntPtr.Zero;
        }
    }

    private void CaptureButton(string button, bool isDown, int x, int y) =>
        ActionCaptured?.Invoke(new MacroAction
        {
            Type = isDown ? MacroActionType.MouseButtonDown : MacroActionType.MouseButtonUp,
            Button = button,
            X = x,
            Y = y
        });

    private static ushort HighWord(uint value) => unchecked((ushort)(value >> 16));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        Unhook(_keyboardHook, "KeyboardHook removal failed");
        _keyboardHook = IntPtr.Zero;
        Unhook(_mouseHook, "MouseHook removal failed");
        _mouseHook = IntPtr.Zero;
        if (_rawInputWindowHookAttached && _rawInputWindowHook is not null && _windowSource is not null)
        {
            try { _windowSource.RemoveHook(_rawInputWindowHook); }
            catch (Exception ex) { FileLogger.Warning("GlobalInputHook.RemoveHook", "RawInput window hook removal failed", ex); }
            _rawInputWindowHookAttached = false;
        }
        if (RelativeMouseCaptureAvailable)
        {
            try
            {
                var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevRemove, Target = IntPtr.Zero };
                if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                    FileLogger.Warning("GlobalInputHook.RegisterRawInputDevices", "RawInput removal failed",
                        win32ErrorCode: Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { FileLogger.Warning("GlobalInputHook.RegisterRawInputDevices", "RawInput removal failed", ex); }
        }
        GC.KeepAlive(_keyboardCallback);
        GC.KeepAlive(_mouseCallback);
    }

    private static void Unhook(IntPtr hook, string message)
    {
        if (hook == IntPtr.Zero) return;
        try
        {
            if (!UnhookWindowsHookEx(hook))
                FileLogger.Warning("GlobalInputHook.UnhookWindowsHookEx", message,
                    win32ErrorCode: Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { FileLogger.Warning("GlobalInputHook.UnhookWindowsHookEx", message, ex); }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseData
    {
        public ushort Flags;
        public ushort Reserved;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint deviceCount, uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
