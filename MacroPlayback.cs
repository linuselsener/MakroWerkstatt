using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO;

namespace MakroWerkstatt;

public sealed record PlaybackPlan(IReadOnlyList<MacroAction> Actions, double Scale, double BaseDurationMs, int TransitionCount,
    bool RandomizerEnabled, int RandomMinMs, int RandomMaxMs)
{
    public bool EvenlyDistribute { get; init; }
    public int RepeatCount { get; init; } = 1;
    public long TotalTransitionCount => Math.Max(0L, (long)Actions.Count * RepeatCount - 1L);
    public double EstimatedMinimumMs => BaseDurationMs * RepeatCount + (RandomizerEnabled ? TotalTransitionCount * (double)RandomMinMs : 0);
    public double EstimatedMaximumMs => BaseDurationMs * RepeatCount + (RandomizerEnabled ? TotalTransitionCount * (double)RandomMaxMs : 0);
    public double EstimatedAverageMs => (EstimatedMinimumMs + EstimatedMaximumMs) / 2d;

    public double DueTimeMs(int actionIndex, MacroAction action) => EvenlyDistribute
        ? BaseDurationMs * actionIndex / Math.Max(1, Actions.Count - 1)
        : action.AtMs * Scale;

    public static PlaybackPlan Create(MacroDocument macro, bool stretchEnabled, double targetDurationMs,
        bool randomizerEnabled, int randomMinMs, int randomMaxMs, int repeatCount = 1)
    {
        var actions = macro.Actions;
        var recordedDuration = actions.Count == 0 ? 0 : Math.Max(0, actions[^1].AtMs);
        var evenlyDistribute = stretchEnabled && recordedDuration == 0 && actions.Count > 1;
        var cannotStretchSingleAction = stretchEnabled && recordedDuration == 0 && actions.Count == 1;
        var baseDuration = stretchEnabled && !cannotStretchSingleAction ? targetDurationMs : recordedDuration;
        var scale = recordedDuration > 0 && stretchEnabled ? targetDurationMs / recordedDuration : 1d;
        return new PlaybackPlan(actions.ToArray(), scale, baseDuration, Math.Max(0, actions.Count - 1),
            randomizerEnabled, randomMinMs, randomMaxMs)
        {
            EvenlyDistribute = evenlyDistribute,
            RepeatCount = repeatCount
        };
    }
}

public static class MacroPlayback
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfWheel = 0x0800;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfUp = 0x80;

    public static async Task RunAsync(PlaybackPlan plan, Action<MacroAction> executeAction, CancellationToken cancellationToken,
        Action<int>? transitionDelayChosen = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var random = Random.Shared;
        long addedRandomDelay = 0;

        long globalActionIndex = 0;
        for (var pass = 0; pass < plan.RepeatCount; pass++)
        {
            for (var index = 0; index < plan.Actions.Count; index++, globalActionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = plan.Actions[index];
                if (globalActionIndex > 0 && plan.RandomizerEnabled)
                {
                    var delay = random.Next(plan.RandomMinMs, plan.RandomMaxMs + 1);
                    addedRandomDelay += delay;
                    transitionDelayChosen?.Invoke(delay);
                }

                var passOffsetMs = pass * plan.BaseDurationMs;
                var dueMs = (long)Math.Round(passOffsetMs + plan.DueTimeMs(index, action), MidpointRounding.AwayFromZero) + addedRandomDelay;
                var waitMs = dueMs - timer.ElapsedMilliseconds;
                if (waitMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                executeAction(action);
            }
        }
    }

    public static void Execute(MacroAction action, bool relativeMouseMode = false)
    {
        switch (action.Type)
        {
            case MacroActionType.KeyDown:
            case MacroActionType.KeyUp:
                SendKey(action);
                break;
            case MacroActionType.MouseMove:
                if (!relativeMouseMode) MoveCursor(action.X, action.Y);
                break;
            case MacroActionType.MouseMoveRelative:
                if (relativeMouseMode) MoveRelative(action.X, action.Y);
                break;
            case MacroActionType.MouseButtonDown:
            case MacroActionType.MouseButtonUp:
                if (!relativeMouseMode) MoveCursor(action.X, action.Y);
                SendMouseButton(action);
                break;
            case MacroActionType.MouseWheel:
                if (!relativeMouseMode) MoveCursor(action.X, action.Y);
                SendMouse(MouseeventfWheel, unchecked((uint)action.WheelDelta));
                break;
            default:
                throw new InvalidDataException("Das Makro enthält einen nicht unterstützten Aktionstyp.");
        }
    }

    private static void SendKey(MacroAction action)
    {
        uint flags = 0;
        if (((uint)action.RawFlags & LlkhfExtended) != 0) flags |= KeyeventfExtendedkey;
        if (action.Type == MacroActionType.KeyUp || ((uint)action.RawFlags & LlkhfUp) != 0) flags |= KeyeventfKeyup;

        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = unchecked((ushort)action.KeyCode),
                    ScanCode = unchecked((ushort)action.ScanCode),
                    Flags = flags
                }
            }
        };
        Send([input]);
    }

    private static void SendMouseButton(MacroAction action)
    {
        var isDown = action.Type == MacroActionType.MouseButtonDown;
        SendMouseButton(action.Button, isDown);
    }

    private static void SendMouseButton(string button, bool isDown)
    {
        var (flags, data) = button switch
        {
            MacroMouseButtons.Left => (isDown ? MouseeventfLeftdown : MouseeventfLeftup, 0u),
            MacroMouseButtons.Right => (isDown ? MouseeventfRightdown : MouseeventfRightup, 0u),
            MacroMouseButtons.Middle => (isDown ? MouseeventfMiddledown : MouseeventfMiddleup, 0u),
            MacroMouseButtons.X1 => (isDown ? MouseeventfXdown : MouseeventfXup, XButton1),
            MacroMouseButtons.X2 => (isDown ? MouseeventfXdown : MouseeventfXup, XButton2),
            _ => throw new InvalidDataException("Das Makro enthält eine unbekannte Maustaste.")
        };
        SendMouse(flags, data);
    }

    public static void ReleaseButton(string button) => SendMouseButton(button, isDown: false);

    private static void MoveCursor(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            var error = Marshal.GetLastWin32Error();
            try { throw new Win32Exception(error, "Die Mausposition konnte nicht gesetzt werden."); }
            catch (Win32Exception ex)
            {
                FileLogger.Error("MacroPlayback.SetCursorPos", "SetCursorPos failed", ex, error);
                throw;
            }
        }
    }

    public static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send([new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = MouseeventfMove } }
        }]);
    }

    private static void SendMouse(uint flags, uint data) => Send([new Input
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { MouseData = data, Flags = flags } }
    }]);

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            try
            {
                throw new Win32Exception(error,
                    "Windows hat die Eingabe nicht angenommen. Geschützte oder mit erhöhten Rechten gestartete Fenster können Eingaben blockieren.");
            }
            catch (Win32Exception ex)
            {
                FileLogger.Error("MacroPlayback.SendInput", "SendInput failed", ex, error);
                throw;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}

/// <summary>Tracks held inputs so stopping during a key or mouse hold releases them.</summary>
public sealed class PlaybackInputState
{
    private const int LlkhfExtended = 0x01;
    private const int LlkhfUp = 0x80;
    private readonly Dictionary<(int KeyCode, bool Extended), MacroAction> _pressedKeys = [];
    private readonly HashSet<string> _pressedButtons = new(StringComparer.Ordinal);
    private readonly bool _relativeMouseMode;
    private readonly bool _hasRecordedRelativeMoves;
    private (int X, int Y)? _lastAbsolutePosition;

    public PlaybackInputState(bool relativeMouseMode = false, bool hasRecordedRelativeMoves = false)
    {
        _relativeMouseMode = relativeMouseMode;
        _hasRecordedRelativeMoves = hasRecordedRelativeMoves;
    }

    public void Execute(MacroAction action)
    {
        if (_relativeMouseMode && action.Type == MacroActionType.MouseMove)
        {
            if (!_hasRecordedRelativeMoves)
            {
                if (_lastAbsolutePosition is { } previous)
                    MacroPlayback.MoveRelative(action.X - previous.X, action.Y - previous.Y);
                _lastAbsolutePosition = (action.X, action.Y);
            }
            return;
        }
        if (action.Type == MacroActionType.MouseMoveRelative && !_relativeMouseMode)
            return;

        MacroPlayback.Execute(action, _relativeMouseMode);
        switch (action.Type)
        {
            case MacroActionType.KeyDown:
                _pressedKeys[(action.KeyCode, (action.RawFlags & LlkhfExtended) != 0)] = action;
                break;
            case MacroActionType.KeyUp:
                _pressedKeys.Remove((action.KeyCode, (action.RawFlags & LlkhfExtended) != 0));
                break;
            case MacroActionType.MouseButtonDown:
                _pressedButtons.Add(action.Button);
                break;
            case MacroActionType.MouseButtonUp:
                _pressedButtons.Remove(action.Button);
                break;
        }
    }

    public void ReleaseAll()
    {
        foreach (var (key, down) in _pressedKeys.ToArray())
        {
            try
            {
                MacroPlayback.Execute(new MacroAction
                {
                    Type = MacroActionType.KeyUp,
                    KeyCode = key.KeyCode,
                    ScanCode = down.ScanCode,
                    RawFlags = down.RawFlags | LlkhfUp
                });
            }
            catch (Exception ex)
            {
                FileLogger.Error("PlaybackInputState.ReleaseAll", "Keyboard release failed", ex);
            }
        }
        _pressedKeys.Clear();

        foreach (var button in _pressedButtons.ToArray())
        {
            try { MacroPlayback.ReleaseButton(button); }
            catch (Exception ex)
            {
                FileLogger.Error("PlaybackInputState.ReleaseAll", "Mouse button release failed", ex);
            }
        }
        _pressedButtons.Clear();
    }
}
