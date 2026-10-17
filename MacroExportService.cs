using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakroWerkstatt;

public sealed class MacroExportService
{
    public const int ExportFormatVersion = 1;
    public const int DefaultIterations = 600_000;
    public const int MinimumIterations = 600_000;
    public const int MaximumIterations = 5_000_000;
    public const int MaximumFileBytes = 50 * 1024 * 1024;
    public const int MaximumMacroCount = 10_000;
    public const int MinimumPassphraseLength = 12;

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int KeyLength = 32;
    private const int TagLength = 16;
    private const string KdfName = "pbkdf2-sha256";
    private const string CipherName = "aes-256-gcm";

    private readonly int _iterations;
    private readonly int _minimumIterations;
    private readonly int _maximumFileBytes;
    private readonly int _maximumMacroCount;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        MaxDepth = 64
    };

    public MacroExportService() : this(DefaultIterations, MinimumIterations, MaximumFileBytes, MaximumMacroCount)
    {
    }

    // This constructor is internal so tests can lower the work factor and input bounds without
    // exposing a way for production callers to accept weaker exports.
    internal MacroExportService(int iterations, int minimumIterations, int maximumFileBytes = MaximumFileBytes,
        int maximumMacroCount = MaximumMacroCount)
    {
        if (minimumIterations < 1 || minimumIterations > MinimumIterations)
            throw new ArgumentOutOfRangeException(nameof(minimumIterations));
        if (iterations < minimumIterations || iterations > MaximumIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        if (maximumFileBytes < 1 || maximumFileBytes > MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (maximumMacroCount < 1 || maximumMacroCount > MaximumMacroCount)
            throw new ArgumentOutOfRangeException(nameof(maximumMacroCount));

        _iterations = iterations;
        _minimumIterations = minimumIterations;
        _maximumFileBytes = maximumFileBytes;
        _maximumMacroCount = maximumMacroCount;
    }

    public byte[] Export(IReadOnlyList<MacroDocument> macros, ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(macros);
        if (passphrase.Length < MinimumPassphraseLength)
            throw new MacroExportException("Die Passphrase muss mindestens 12 Zeichen lang sein.");
        if (macros.Count > _maximumMacroCount)
            throw new MacroExportException("Die Bibliothek enthält zu viele Makros für einen Export.");

        var documents = new List<MacroDocument>(macros.Count);
        try
        {
            foreach (var macro in macros)
            {
                ArgumentNullException.ThrowIfNull(macro);
                var copy = CloneAsVersionTwo(macro);
                MacroStorage.ValidateDocument(copy);
                documents.Add(copy);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new MacroExportException("Ein oder mehrere Makros sind ungültig und konnten nicht exportiert werden.");
        }

        byte[]? plaintext = null;
        byte[]? key = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? associatedData = null;
        var salt = new byte[SaltLength];
        var nonce = new byte[NonceLength];
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new ExportPayload
            {
                ExportFormat = ExportFormatVersion,
                Macros = documents
            }, _jsonOptions);
            if (plaintext.Length > _maximumFileBytes)
                throw new MacroExportException("Der Export ist größer als die zulässige Dateigröße.");

            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);
            key = new byte[KeyLength];
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, _iterations, HashAlgorithmName.SHA256);
            ciphertext = new byte[plaintext.Length];
            tag = new byte[TagLength];
            var header = new ExportHeader
            {
                ExportFormat = ExportFormatVersion,
                Kdf = KdfName,
                Iterations = _iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Cipher = CipherName
            };
            associatedData = SerializeHeader(header);
            using (var aes = new AesGcm(key, TagLength))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var exportFile = JsonSerializer.SerializeToUtf8Bytes(new ExportEnvelope
            {
                ExportFormat = header.ExportFormat,
                Kdf = header.Kdf,
                Iterations = header.Iterations,
                Salt = header.Salt,
                Nonce = header.Nonce,
                Cipher = header.Cipher,
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            }, _jsonOptions);
            if (exportFile.Length > _maximumFileBytes)
            {
                CryptographicOperations.ZeroMemory(exportFile);
                throw new MacroExportException("Der Export ist größer als die zulässige Dateigröße.");
            }
            return exportFile;
        }
        catch (MacroExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new MacroExportException("Der Export konnte nicht erstellt werden.");
        }
        finally
        {
            Zero(plaintext);
            Zero(key);
            Zero(ciphertext);
            Zero(tag);
            Zero(associatedData);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public ImportResult Import(byte[] file, ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length > _maximumFileBytes)
            throw new MacroExportException("Die Exportdatei ist größer als 50 MB.");
        if (passphrase.Length < MinimumPassphraseLength)
            throw new MacroExportException("Die Passphrase muss mindestens 12 Zeichen lang sein.");

        ExportHeader header;
        byte[] salt;
        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;
        try
        {
            (header, salt, nonce, ciphertext, tag) = ReadEnvelope(file);
        }
        catch (MacroExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw InvalidExport();
        }

        byte[]? key = null;
        byte[]? plaintext = null;
        byte[]? associatedData = null;
        try
        {
            key = new byte[KeyLength];
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, header.Iterations, HashAlgorithmName.SHA256);
            plaintext = new byte[ciphertext.Length];
            associatedData = SerializeHeader(header);
            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }
            catch (CryptographicException)
            {
                throw AuthenticationFailure();
            }

            if (plaintext.Length > _maximumFileBytes)
                throw InvalidExport();
            return ReadPayload(plaintext);
        }
        catch (MacroExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw InvalidExport();
        }
        finally
        {
            Zero(key);
            Zero(plaintext);
            Zero(associatedData);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private ImportResult ReadPayload(byte[] plaintext)
    {
        try
        {
            using var payload = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 64 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("exportFormat", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != ExportFormatVersion
                || !payload.RootElement.TryGetProperty("macros", out var macrosElement)
                || macrosElement.ValueKind != JsonValueKind.Array)
                throw InvalidExport();

            if (macrosElement.GetArrayLength() > _maximumMacroCount)
                throw new MacroExportException("Die Exportdatei enthält zu viele Makros.");

            var imported = new List<MacroDocument>(macrosElement.GetArrayLength());
            var invalid = 0;
            foreach (var item in macrosElement.EnumerateArray())
            {
                try
                {
                    var macro = item.Deserialize<MacroDocument>(_jsonOptions);
                    if (macro is null || macro.FormatVersion != MacroStorage.PayloadFormatVersion)
                        throw new InvalidDataException();
                    MacroStorage.ValidateDocument(macro);
                    imported.Add(macro);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    invalid++;
                }
            }

            return new ImportResult(imported, invalid, invalid);
        }
        catch (MacroExportException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidExport();
        }
    }

    private (ExportHeader Header, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag) ReadEnvelope(byte[] file)
    {
        using var json = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw InvalidExport();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Name is not ("exportFormat" or "kdf" or "iterations" or "salt" or "nonce" or "cipher" or "ciphertext" or "tag"))
                throw InvalidExport();
        }
        if (names.Count != 8) throw InvalidExport();

        var root = json.RootElement;
        var exportFormat = RequiredInt(root, "exportFormat");
        if (exportFormat != ExportFormatVersion) throw InvalidExport();
        var kdf = RequiredString(root, "kdf");
        if (!string.Equals(kdf, KdfName, StringComparison.Ordinal)) throw InvalidExport();
        var iterations = RequiredInt(root, "iterations");
        if (iterations < _minimumIterations || iterations > MaximumIterations) throw InvalidExport();
        var cipher = RequiredString(root, "cipher");
        if (!string.Equals(cipher, CipherName, StringComparison.Ordinal)) throw InvalidExport();

        byte[]? salt = null;
        byte[]? nonce = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        try
        {
            salt = DecodeBase64(RequiredString(root, "salt"));
            nonce = DecodeBase64(RequiredString(root, "nonce"));
            ciphertext = DecodeBase64(RequiredString(root, "ciphertext"));
            tag = DecodeBase64(RequiredString(root, "tag"));
            if (salt.Length != SaltLength || nonce.Length != NonceLength || tag.Length != TagLength
                || ciphertext.Length > _maximumFileBytes)
                throw InvalidExport();

            var result = (new ExportHeader
            {
                ExportFormat = exportFormat,
                Kdf = kdf,
                Iterations = iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Cipher = cipher
            }, salt, nonce, ciphertext, tag);
            salt = null;
            nonce = null;
            ciphertext = null;
            tag = null;
            return result;
        }
        finally
        {
            Zero(salt);
            Zero(nonce);
            Zero(ciphertext);
            Zero(tag);
        }
    }

    private byte[] SerializeHeader(ExportHeader header) => JsonSerializer.SerializeToUtf8Bytes(header, _jsonOptions);

    private static int RequiredInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)) throw InvalidExport();
        return value;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw InvalidExport();
        return element.GetString() ?? throw InvalidExport();
    }

    private static byte[] DecodeBase64(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw InvalidExport(); }
    }

    private MacroDocument CloneAsVersionTwo(MacroDocument macro)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(macro, _jsonOptions);
        try
        {
            var clone = JsonSerializer.Deserialize<MacroDocument>(bytes, _jsonOptions)
                ?? throw InvalidExport();
            clone.FormatVersion = MacroStorage.PayloadFormatVersion;
            return clone;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static MacroExportException InvalidExport() => new("Die Exportdatei ist ungültig oder wird nicht unterstützt.");
    private static MacroExportException AuthenticationFailure() => new("Falsche Passphrase oder beschädigte Datei.");
    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class ExportPayload
    {
        public int ExportFormat { get; set; }
        public IReadOnlyList<MacroDocument> Macros { get; set; } = [];
    }

    private class ExportHeader
    {
        public int ExportFormat { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Cipher { get; set; } = "";
    }

    private sealed class ExportEnvelope : ExportHeader
    {
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }
}

public sealed record ImportResult(IReadOnlyList<MacroDocument> Macros, int RejectedCount, int InvalidCount);

public sealed class MacroExportException(string message) : Exception(message);
