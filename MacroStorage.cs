using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace MakroWerkstatt;

public sealed class MacroStorage
{
    public const int CurrentFormatVersion = 3;
    public const int PayloadFormatVersion = 2;
    private const int PreviousFormatVersion = 1;
    private const string CurrentEncryption = "dpapi-currentuser";
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
    private readonly IDataProtector _dataProtector;
    private readonly IMacroFileOperations _fileOperations;

    public string RootPath { get; }
    public string MacroFolder { get; }
    public string SettingsPath { get; }

    public MacroStorage(string? rootPath = null, IDataProtector? dataProtector = null)
        : this(rootPath, dataProtector, PhysicalMacroFileOperations.Instance)
    {
    }

    internal MacroStorage(string? rootPath, IDataProtector? dataProtector, IMacroFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(fileOperations);
        RootPath = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakroWerkstatt");
        MacroFolder = Path.Combine(RootPath, "Makros");
        SettingsPath = Path.Combine(RootPath, "settings.json");
        _dataProtector = dataProtector ?? new DpapiDataProtector();
        _fileOperations = fileOperations;
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
                items.Add(Load(path));
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                LogFailure("MacroStorage.Load", ex);
                issues.Add($"{Path.GetFileName(path)}: {GetSafeLoadFailure(ex)}");
            }
        }

        warnings = issues;
        return items.OrderByDescending(item => item.UpdatedUtc).ToArray();
    }

    public MacroDocument LoadById(Guid id)
    {
        try
        {
            return Load(GetMacroPath(id));
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            LogFailure("MacroStorage.LoadById", ex);
            throw;
        }
    }

    public void Save(MacroDocument document, bool encrypt = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsSupportedDocumentVersion(document.FormatVersion))
            throw new InvalidDataException("Dieses Makro verwendet eine nicht unterstützte Formatversion.");

        document.FormatVersion = PayloadFormatVersion;
        Validate(document);
        Directory.CreateDirectory(MacroFolder);
        var destination = GetMacroPath(document.Id);
        byte[]? serialized = null;
        byte[]? protectedPayload = null;
        byte[]? fileBytes = null;
        try
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions);
            fileBytes = encrypt
                ? CreateEnvelope(serialized, out protectedPayload)
                : serialized;
            WriteAndVerifyAtomically(destination, fileBytes, document);
        }
        catch (Exception ex)
        {
            LogFailure("MacroStorage.Save", ex);
            throw;
        }
        finally
        {
            if (serialized is not null) CryptographicOperations.ZeroMemory(serialized);
            if (protectedPayload is not null) CryptographicOperations.ZeroMemory(protectedPayload);
            if (fileBytes is not null) CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    public int CountPlaintextMacros()
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(MacroFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var bytes = ReadFileBytes(path);
                try
                {
                    if (TryGetPlaintextVersion(bytes, out _)) count++;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                LogFailure("MacroStorage.CountPlaintextMacros", ex);
            }
        }
        return count;
    }

    public MacroMigrationResult EncryptExistingPlaintextMacros()
    {
        var succeeded = 0;
        var failed = 0;
        foreach (var path in Directory.EnumerateFiles(MacroFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            byte[]? originalBytes = null;
            try
            {
                originalBytes = ReadFileBytes(path);
                if (!TryGetPlaintextVersion(originalBytes, out _)) continue;

                var document = ReadPlaintextDocument(originalBytes);
                document.FormatVersion = PayloadFormatVersion;
                Validate(document);

                byte[]? plaintext = null;
                byte[]? protectedPayload = null;
                byte[]? envelope = null;
                try
                {
                    plaintext = JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions);
                    envelope = CreateEnvelope(plaintext, out protectedPayload);
                    WriteAndVerifyAtomically(path, envelope, document);
                    succeeded++;
                }
                finally
                {
                    if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                    if (protectedPayload is not null) CryptographicOperations.ZeroMemory(protectedPayload);
                    if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
                }
            }
            catch (Exception ex) when (IsStorageException(ex) || ex is not OutOfMemoryException and not StackOverflowException)
            {
                failed++;
                LogFailure("MacroStorage.Migrate", ex);
            }
            finally
            {
                if (originalBytes is not null) CryptographicOperations.ZeroMemory(originalBytes);
            }
        }

        return new MacroMigrationResult(succeeded, failed);
    }

    public void Delete(Guid id)
    {
        var path = GetMacroPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    internal bool HasStoredFile(Guid id) => File.Exists(GetMacroPath(id));

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
        var bytes = ReadFileBytes(path);
        try
        {
            return ReadDocument(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private MacroDocument ReadDocument(byte[] bytes)
    {
        using var json = ParseJson(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Die Datei enthält kein Makro.");

        var version = GetFormatVersion(json.RootElement);
        if (version > CurrentFormatVersion)
            throw UnsupportedVersion(version);
        if (version == CurrentFormatVersion)
            return ReadEncryptedEnvelope(json.RootElement);
        if (!IsSupportedDocumentVersion(version))
            throw UnsupportedVersion(version);

        return ReadPlaintextDocument(bytes);
    }

    private MacroDocument ReadEncryptedEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("encryption", out var encryptionElement)
            || encryptionElement.ValueKind != JsonValueKind.String
            || !string.Equals(encryptionElement.GetString(), CurrentEncryption, StringComparison.Ordinal))
            throw new InvalidDataException("Das Verschlüsselungsverfahren dieses Makros wird nicht unterstützt.");
        if (!root.TryGetProperty("payload", out var payloadElement)
            || payloadElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Das verschlüsselte Makro ist unvollständig oder beschädigt.");

        var encodedPayload = payloadElement.GetString();
        if (string.IsNullOrEmpty(encodedPayload) || encodedPayload.Length > MaximumMacroFileBytes)
            throw new InvalidDataException("Das verschlüsselte Makro ist unvollständig oder beschädigt.");

        byte[] protectedData;
        try
        {
            protectedData = Convert.FromBase64String(encodedPayload);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Das verschlüsselte Makro enthält ungültige Daten.");
        }

        if (protectedData.Length > MaximumMacroFileBytes)
        {
            CryptographicOperations.ZeroMemory(protectedData);
            throw new InvalidDataException("Das verschlüsselte Makro ist größer als 100 MB.");
        }

        byte[]? plaintext = null;
        try
        {
            try
            {
                plaintext = _dataProtector.Unprotect(protectedData);
            }
            catch (MacroDecryptionException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                throw new MacroDecryptionException();
            }

            if (plaintext.Length > MaximumMacroFileBytes)
                throw new InvalidDataException("Das entschlüsselte Makro ist größer als 100 MB.");

            var document = ReadPlaintextDocument(plaintext);
            if (document.FormatVersion != PayloadFormatVersion)
                throw new InvalidDataException("Das entschlüsselte Makro verwendet keine unterstützte Formatversion.");
            Validate(document);
            return document;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedData);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private MacroDocument ReadPlaintextDocument(byte[] bytes)
    {
        MacroDocument document;
        try
        {
            document = JsonSerializer.Deserialize<MacroDocument>(bytes, _jsonOptions)
                ?? throw new InvalidDataException("Die Datei enthält kein Makro.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Die Makrodatei enthält ungültiges JSON.");
        }

        if (!IsSupportedDocumentVersion(document.FormatVersion))
            throw UnsupportedVersion(document.FormatVersion);
        Validate(document);
        return document;
    }

    private void WriteAndVerifyAtomically(string destination, byte[] fileBytes, MacroDocument expectedDocument)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Der Makroordner fehlt.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _fileOperations.WriteAllBytes(temporary, fileBytes);
            var verified = Load(temporary);
            if (!DocumentsMatch(expectedDocument, verified))
                throw new InvalidDataException("Das gespeicherte Makro konnte nicht zuverlässig zurückgelesen werden.");

            if (File.Exists(destination)) _fileOperations.Replace(temporary, destination);
            else _fileOperations.Move(temporary, destination);
        }
        finally
        {
            _fileOperations.TryDelete(temporary);
        }
    }

    private byte[] CreateEnvelope(byte[] plaintext, out byte[] protectedPayload)
    {
        try
        {
            protectedPayload = _dataProtector.Protect(plaintext);
        }
        catch (MacroProtectionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw new MacroProtectionException();
        }

        try
        {
            var envelope = new MacroEnvelope
            {
                FormatVersion = CurrentFormatVersion,
                Encryption = CurrentEncryption,
                Payload = Convert.ToBase64String(protectedPayload)
            };
            return JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(protectedPayload);
            protectedPayload = [];
            throw;
        }
    }

    private byte[] ReadFileBytes(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumMacroFileBytes)
            throw new InvalidDataException("Die Datei ist größer als 100 MB und wurde nicht geöffnet.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumMacroFileBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("Die Datei ist größer als 100 MB und wurde nicht geöffnet.");
        }
        return bytes;
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Die Makrodatei enthält ungültiges JSON.");
        }
    }

    private static int GetFormatVersion(JsonElement root)
    {
        if (!root.TryGetProperty("formatVersion", out var versionElement)) return PayloadFormatVersion;
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
            throw new InvalidDataException("Die Makrodatei enthält eine ungültige Formatversion.");
        return version;
    }

    private static bool TryGetPlaintextVersion(byte[] bytes, out int version)
    {
        version = 0;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            version = GetFormatVersion(json.RootElement);
            return IsSupportedDocumentVersion(version);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static InvalidDataException UnsupportedVersion(int version) =>
        new($"Formatversion {version} wird nicht unterstützt (aktuell: {CurrentFormatVersion}).");

    private static string GetSafeLoadFailure(Exception exception) => exception switch
    {
        MacroDecryptionException decryption => decryption.Message,
        MacroProtectionException => "Das Makro konnte nicht verschlüsselt werden.",
        InvalidDataException invalidData => invalidData.Message,
        UnauthorizedAccessException => "Auf die Datei konnte nicht zugegriffen werden.",
        IOException => "Die Datei konnte nicht gelesen werden.",
        NotSupportedException => "Das Dateiformat wird nicht unterstützt.",
        _ => "Die Datei konnte nicht geöffnet werden."
    };

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or NotSupportedException or MacroProtectionException or MacroDecryptionException;

    private static void LogFailure(string component, Exception exception)
    {
        FileLogger.Error(component, $"Fehlertyp={exception.GetType().Name}");
    }

    private static bool DocumentsMatch(MacroDocument expected, MacroDocument actual)
    {
        if (expected.FormatVersion != actual.FormatVersion || expected.Application != actual.Application
            || expected.Id != actual.Id || expected.Name != actual.Name
            || expected.CreatedUtc != actual.CreatedUtc || expected.UpdatedUtc != actual.UpdatedUtc
            || expected.Actions.Count != actual.Actions.Count)
            return false;

        for (var index = 0; index < expected.Actions.Count; index++)
        {
            var left = expected.Actions[index];
            var right = actual.Actions[index];
            if (left.AtMs != right.AtMs || left.Type != right.Type || left.X != right.X || left.Y != right.Y
                || left.KeyCode != right.KeyCode || left.ScanCode != right.ScanCode || left.RawFlags != right.RawFlags
                || left.WheelDelta != right.WheelDelta || !string.Equals(left.Button, right.Button, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    internal static void ValidateDocument(MacroDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
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

    private static bool IsSupportedDocumentVersion(int version) => version is PreviousFormatVersion or PayloadFormatVersion;

    private sealed class MacroEnvelope
    {
        public int FormatVersion { get; set; }
        public string Encryption { get; set; } = "";
        public string Payload { get; set; } = "";
    }
}

public sealed record MacroMigrationResult(int Succeeded, int Failed);

internal interface IMacroFileOperations
{
    void WriteAllBytes(string path, byte[] content);
    void Replace(string temporaryPath, string destinationPath);
    void Move(string temporaryPath, string destinationPath);
    void TryDelete(string path);
}

internal sealed class PhysicalMacroFileOperations : IMacroFileOperations
{
    public static PhysicalMacroFileOperations Instance { get; } = new();

    private PhysicalMacroFileOperations() { }

    public void WriteAllBytes(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string temporaryPath, string destinationPath) => File.Replace(temporaryPath, destinationPath, null);

    public void Move(string temporaryPath, string destinationPath) => File.Move(temporaryPath, destinationPath);

    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class MacroSettings
{
    public bool StretchEnabled { get; set; } = true;
    public string Duration { get; set; } = "1";
    public int DurationUnitIndex { get; set; }
    public bool RandomizerEnabled { get; set; }
    public bool RelativeMouseMovement { get; set; }
    public bool EncryptMacros { get; set; } = true;
    public string RepeatCount { get; set; } = "1";
    public string RandomMinMs { get; set; } = "1";
    public string RandomMaxMs { get; set; } = "80";
    public string RecordingHotkey { get; set; } = "F9";
    public string PlaybackHotkey { get; set; } = "F10";
}
