using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace MakroWerkstatt;

public sealed class MacroStorage
{
    public const int CurrentFormatVersion = 2;
    private const int PreviousFormatVersion = 1;
    private const long MaximumMacroFileBytes = 100 * 1024 * 1024;
    public const int MaximumActionCount = 500_000;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string RootPath { get; }
    public string MacroFolder { get; }
    public string SettingsPath { get; }

    public MacroStorage(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakroWerkstatt");
        MacroFolder = Path.Combine(RootPath, "Makros");
        SettingsPath = Path.Combine(RootPath, "settings.json");
        Directory.CreateDirectory(MacroFolder);
    }

    public IReadOnlyList<MacroDocument> LoadAll(out IReadOnlyList<string> warnings)
    {
        var items = new List<MacroDocument>();
        var issues = new List<string>();
        Directory.CreateDirectory(MacroFolder);
        foreach (var path in Directory.EnumerateFiles(MacroFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = Load(path);
                items.Add(document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            {
                issues.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        warnings = issues;
        return items.OrderByDescending(item => item.UpdatedUtc).ToArray();
    }

    public MacroDocument LoadById(Guid id)
    {
        var path = GetMacroPath(id);
        return Load(path);
    }

    public void Save(MacroDocument document)
    {
        if (!IsSupportedFormatVersion(document.FormatVersion))
            throw new InvalidDataException("Dieses Makro verwendet eine nicht unterstützte Formatversion.");
        document.FormatVersion = CurrentFormatVersion;
        Validate(document);
        Directory.CreateDirectory(MacroFolder);

        var destination = GetMacroPath(document.Id);
        var temporary = destination + ".tmp";
        var json = JsonSerializer.Serialize(document, _jsonOptions);
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, destination, overwrite: true);
    }

    public void Delete(Guid id)
    {
        var path = GetMacroPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    public MacroSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath) || new FileInfo(SettingsPath).Length > 64 * 1024)
                return new MacroSettings();
            return JsonSerializer.Deserialize<MacroSettings>(File.ReadAllText(SettingsPath), _jsonOptions) ?? new MacroSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new MacroSettings();
        }
    }

    public void SaveSettings(MacroSettings settings)
    {
        Directory.CreateDirectory(RootPath);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, _jsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    private MacroDocument Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumMacroFileBytes)
            throw new InvalidDataException("Die Datei ist größer als 100 MB und wurde nicht geöffnet.");
        var document = JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(path), _jsonOptions)
            ?? throw new InvalidDataException("Die Datei enthält kein Makro.");
        if (!IsSupportedFormatVersion(document.FormatVersion))
            throw new InvalidDataException($"Formatversion {document.FormatVersion} wird nicht unterstützt (aktuell: {CurrentFormatVersion}).");
        Validate(document);
        return document;
    }

    private static void Validate(MacroDocument document)
    {
        if (document.Id == Guid.Empty)
            throw new InvalidDataException("Die Makro-ID fehlt.");
        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 100)
            throw new InvalidDataException("Der Makroname muss zwischen 1 und 100 Zeichen lang sein.");
        if (document.Actions is null || document.Actions.Count > MaximumActionCount)
            throw new InvalidDataException($"Ein Makro darf höchstens {MaximumActionCount:N0} Aktionen enthalten.");
        if (document.Actions.Any(action => action is null))
            throw new InvalidDataException("Das Makro enthält einen leeren Aktionseintrag.");

        long previous = -1;
        for (var index = 0; index < document.Actions.Count; index++)
        {
            var action = document.Actions[index];
            if (action.AtMs < 0 || action.AtMs < previous)
                throw new InvalidDataException("Die Zeitstempel müssen aufsteigend und nicht negativ sein.");
            if (!Enum.IsDefined(action.Type))
                throw new InvalidDataException("Das Makro enthält einen unbekannten Aktionstyp.");
            if (document.FormatVersion == PreviousFormatVersion && action.Type == MacroActionType.MouseMoveRelative)
                throw new InvalidDataException("Relative Mausbewegungen benötigen Makroformatversion 2.");
            if (action.KeyCode is < 0 or > 255 || action.ScanCode is < 0 or > 65535)
                throw new InvalidDataException("Das Makro enthält einen ungültigen Tastencode.");
            if ((action.Type is MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp)
                && !MacroMouseButtons.IsAllowed(action.Button))
                throw new InvalidDataException($"Aktion {index + 1}: unbekannte Maustaste.");
            if (action.Button is null || action.Button.Length > 16)
                throw new InvalidDataException("Das Makro enthält einen ungültigen Maustastencode.");
            previous = action.AtMs;
        }
    }

    private string GetMacroPath(Guid id) => Path.Combine(MacroFolder, $"{id:N}.json");

    private static bool IsSupportedFormatVersion(int version) => version is PreviousFormatVersion or CurrentFormatVersion;
}

public sealed class MacroSettings
{
    public bool StretchEnabled { get; set; } = true;
    public string Duration { get; set; } = "1";
    public int DurationUnitIndex { get; set; }
    public bool RandomizerEnabled { get; set; }
    public bool RelativeMouseMovement { get; set; }
    public string RepeatCount { get; set; } = "1";
    public string RandomMinMs { get; set; } = "1";
    public string RandomMaxMs { get; set; } = "80";
    public string RecordingHotkey { get; set; } = "F9";
    public string PlaybackHotkey { get; set; } = "F10";
}
