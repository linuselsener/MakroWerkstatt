using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace MakroWerkstatt;

public sealed class MacroDocument
{
    public int FormatVersion { get; set; } = 2;
    public string Application { get; set; } = "MakroWerkstatt";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Neue Aufnahme";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MacroAction> Actions { get; set; } = [];

    [JsonIgnore]
    public string ActionSummary => Actions.Count == 1 ? "1 Aktion" : $"{Actions.Count:N0} Aktionen";

    [JsonIgnore]
    public string UpdatedLocalText => UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy · HH:mm", CultureInfo.GetCultureInfo("de-DE"));
}

public sealed class MacroAction
{
    public long AtMs { get; set; }
    public MacroActionType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int KeyCode { get; set; }
    public int ScanCode { get; set; }
    public int RawFlags { get; set; }
    public int WheelDelta { get; set; }
    public string Button { get; set; } = "";

    [JsonIgnore]
    public string DisplayTime
    {
        get
        {
            var totalHours = Math.Max(0, AtMs / 3_600_000);
            var remainder = TimeSpan.FromMilliseconds(Math.Max(0, AtMs) % 3_600_000);
            return $"{totalHours:00}:{remainder.Minutes:00}:{remainder.Seconds:00}.{remainder.Milliseconds:000}";
        }
    }

    [JsonIgnore]
    public string DisplayType => Type switch
    {
        MacroActionType.KeyDown => "Taste drücken",
        MacroActionType.KeyUp => "Taste loslassen",
        MacroActionType.MouseMove => "Maus bewegen",
        MacroActionType.MouseMoveRelative => "Relative Mausbewegung",
        MacroActionType.MouseButtonDown => "Maustaste drücken",
        MacroActionType.MouseButtonUp => "Maustaste loslassen",
        MacroActionType.MouseWheel => "Mausrad",
        _ => "Unbekannte Aktion"
    };

    [JsonIgnore]
    public string DisplayDetails => Type switch
    {
        MacroActionType.KeyDown or MacroActionType.KeyUp => GetKeyName(),
        MacroActionType.MouseMove => $"Position  X {X}, Y {Y}",
        MacroActionType.MouseMoveRelative => $"Bewegung  ΔX {X}, ΔY {Y}",
        MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp => $"{GetButtonName()}  ·  X {X}, Y {Y}",
        MacroActionType.MouseWheel => $"{(WheelDelta >= 0 ? "Nach oben" : "Nach unten")}  ·  {Math.Abs(WheelDelta)}  ·  X {X}, Y {Y}",
        _ => ""
    };

    private string GetKeyName()
    {
        var key = KeyInterop.KeyFromVirtualKey(KeyCode);
        var name = key == Key.None ? $"VK 0x{KeyCode:X2}" : key.ToString();
        return $"{name}  ·  VK 0x{KeyCode:X2}";
    }

    private string GetButtonName() => Button switch
    {
        "Left" => "Linke Taste",
        "Right" => "Rechte Taste",
        "Middle" => "Mittlere Taste",
        "X1" => "Seitentaste 1",
        "X2" => "Seitentaste 2",
        _ => Button
    };
}

public enum MacroActionType
{
    KeyDown,
    KeyUp,
    MouseMove,
    MouseButtonDown,
    MouseButtonUp,
    MouseWheel,
    MouseMoveRelative
}

public static class MacroMouseButtons
{
    public const string Left = "Left";
    public const string Right = "Right";
    public const string Middle = "Middle";
    public const string X1 = "X1";
    public const string X2 = "X2";

    public static IReadOnlyList<string> AllowedValues { get; } = Array.AsReadOnly(
        new[] { Left, Right, Middle, X1, X2 });

    public static bool IsAllowed(string? value) =>
        value is not null && AllowedValues.Contains(value, StringComparer.Ordinal);
}
