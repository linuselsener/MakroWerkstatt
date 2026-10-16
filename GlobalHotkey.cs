namespace MakroWerkstatt;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

public enum GlobalHotkeyAction
{
    ToggleRecording,
    TogglePlayback
}

/// <summary>A configurable global shortcut using one F1–F12 key and optional modifiers.</summary>
public sealed record GlobalHotkey(int VirtualKey, HotkeyModifiers Modifiers)
{
    public static bool TryParse(string? value, out GlobalHotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;

        var modifiers = HotkeyModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }

        var keyName = parts[^1].ToUpperInvariant();
        if (keyName.Length is < 2 or > 3 || keyName[0] != 'F' ||
            !int.TryParse(keyName.AsSpan(1), out var functionKey) || functionKey is < 1 or > 12)
            return false;

        hotkey = new GlobalHotkey(0x70 + functionKey - 1, modifiers);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add($"F{VirtualKey - 0x70 + 1}");
        return string.Join(" + ", parts);
    }
}
