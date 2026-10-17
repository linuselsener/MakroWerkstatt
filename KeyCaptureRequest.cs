namespace MakroWerkstatt;

public enum KeyCaptureTarget
{
    None,
    RecordingHotkey,
    PlaybackHotkey,
    SelectedAction
}

public sealed record KeyCaptureRequest(KeyCaptureTarget Target, int VirtualKey, HotkeyModifiers Modifiers);
