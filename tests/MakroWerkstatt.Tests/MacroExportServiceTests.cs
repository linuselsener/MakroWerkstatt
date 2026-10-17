using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class MacroExportServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private const string TestPassphrase = "synthetic test passphrase";
    private static MacroExportService FastService(int maxBytes = MacroExportService.MaximumFileBytes,
        int maxMacros = MacroExportService.MaximumMacroCount) => new(100, 10, maxBytes, maxMacros);

    [Fact]
    public void Export_and_import_roundtrip_multiple_documents_and_empty_actions()
    {
        var service = FastService();
        var expected = new[]
        {
            Document("synthetic multilingual – äöü 漢字 🚀", [new MacroAction { AtMs = 7, Type = MacroActionType.KeyDown, KeyCode = 65 }]),
            Document("synthetic empty", [])
        };
        byte[]? file = null;
        try
        {
            file = service.Export(expected, TestPassphrase);
            var result = service.Import(file, TestPassphrase);
            Assert.Equal(2, result.Macros.Count);
            Assert.Equal(0, result.InvalidCount);
            Assert.Equal(0, result.RejectedCount);
            for (var index = 0; index < expected.Length; index++) AssertDocumentsEqual(expected[index], result.Macros[index]);
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Production_iteration_count_completes_a_roundtrip()
    {
        var service = new MacroExportService();
        byte[]? file = null;
        try
        {
            var expected = Document("synthetic production KDF", [new MacroAction { Type = MacroActionType.KeyUp, KeyCode = 66 }]);
            file = service.Export([expected], TestPassphrase);
            var result = service.Import(file, TestPassphrase);
            Assert.Single(result.Macros);
            AssertDocumentsEqual(expected, result.Macros[0]);
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Export_file_does_not_contain_plaintext_markers()
    {
        var service = FastService();
        const string marker = "synthetic-flag73";
        var document = Document($"synthetic-{marker}", [new MacroAction { Type = MacroActionType.KeyDown, Button = marker }]);
        byte[]? file = null;
        try
        {
            file = service.Export([document], TestPassphrase);
            var bytesAsText = Encoding.UTF8.GetString(file);
            Assert.True(!bytesAsText.Contains(marker, StringComparison.Ordinal), "Export contains unencrypted content.");
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Wrong_passphrase_returns_neutral_authentication_failure()
    {
        var service = FastService();
        byte[]? file = null;
        var wrongPassphrase = "other synthetic passphrase".ToCharArray();
        try
        {
            file = service.Export([Document("synthetic protected name", [])], TestPassphrase);
            var error = Assert.Throws<MacroExportException>(() => service.Import(file, wrongPassphrase));
            Assert.Equal("Falsche Passphrase oder beschädigte Datei.", error.Message);
            Assert.DoesNotContain("synthetic", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Zero(file);
            ZeroChars(wrongPassphrase);
        }
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("iterations")]
    [InlineData("salt")]
    [InlineData("nonce")]
    public void Changed_authenticated_bytes_are_rejected(string field)
    {
        var service = FastService();
        byte[]? file = null;
        try
        {
            file = service.Export([Document("synthetic tamper check", [])], TestPassphrase);
            var changed = MutateField(file, field);
            var error = Assert.Throws<MacroExportException>(() => service.Import(changed, TestPassphrase));
            Assert.Equal("Falsche Passphrase oder beschädigte Datei.", error.Message);
            Zero(changed);
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Import_rejects_iterations_outside_configured_range_and_wrong_lengths()
    {
        var service = FastService();
        byte[]? file = null;
        try
        {
            file = service.Export([Document("synthetic hardening", [])], TestPassphrase);
            foreach (var (field, value) in new (string, JsonNode?)[]
            {
                ("iterations", JsonValue.Create(9)),
                ("iterations", JsonValue.Create(MacroExportService.MaximumIterations + 1)),
                ("salt", JsonValue.Create(Convert.ToBase64String(new byte[15]))),
                ("nonce", JsonValue.Create(Convert.ToBase64String(new byte[11]))),
                ("tag", JsonValue.Create(Convert.ToBase64String(new byte[15])))
            })
            {
                var changed = SetField(file, field, value);
                Assert.Throws<MacroExportException>(() => service.Import(changed, TestPassphrase));
                Zero(changed);
            }
        }
        finally { Zero(file); }
    }

    [Theory]
    [InlineData("kdf", "unknown-kdf")]
    [InlineData("cipher", "unknown-cipher")]
    public void Import_rejects_unknown_algorithms(string field, string value)
    {
        var service = FastService();
        byte[]? file = null;
        try
        {
            file = service.Export([Document("synthetic algorithm test", [])], TestPassphrase);
            var changed = SetField(file, field, JsonValue.Create(value));
            Assert.Throws<MacroExportException>(() => service.Import(changed, TestPassphrase));
            Zero(changed);
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Import_rejects_future_export_format_version()
    {
        var service = FastService();
        byte[]? file = null;
        try
        {
            file = service.Export([Document("synthetic future format", [])], TestPassphrase);
            var changed = SetField(file, "exportFormat", JsonValue.Create(2));
            Assert.Throws<MacroExportException>(() => service.Import(changed, TestPassphrase));
            Zero(changed);
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Import_rejects_files_over_configured_limit_before_parsing()
    {
        var standard = FastService();
        byte[]? file = null;
        try
        {
            file = standard.Export([Document("synthetic size check", [])], TestPassphrase);
            var constrained = FastService(maxBytes: file.Length - 1);
            Assert.Throws<MacroExportException>(() => constrained.Import(file, TestPassphrase));
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Import_file_rejects_oversized_input_before_reading_it_into_memory()
    {
        using var context = new TestContext();
        var path = Path.Combine(context.RootPath, "oversized.mwexport");
        File.WriteAllBytes(path, new byte[65]);

        var error = Assert.Throws<MacroExportException>(() => new MacroExportService(10, 10, maximumFileBytes: 64)
            .ImportFile(path, TestPassphrase));

        Assert.Contains("größer als 50 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_rejects_too_many_macros_before_document_deserialization()
    {
        var service = FastService(maxMacros: 1);
        byte[]? file = null;
        try
        {
            file = CreateExportFile([Document("synthetic first", []), Document("synthetic second", [])]);
            Assert.Throws<MacroExportException>(() => service.Import(file, TestPassphrase));
        }
        finally { Zero(file); }
    }

    [Fact]
    public void Import_rejects_non_json_export_cleanly()
    {
        var service = FastService();
        var error = Assert.Throws<MacroExportException>(() => service.Import(Encoding.UTF8.GetBytes("not json"), TestPassphrase));
        Assert.DoesNotContain("not json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_export_uses_fresh_salt_nonce_and_ciphertext()
    {
        var service = FastService();
        byte[]? first = null;
        byte[]? second = null;
        try
        {
            var macros = new[] { Document("synthetic randomness", []) };
            first = service.Export(macros, TestPassphrase);
            second = service.Export(macros, TestPassphrase);
            using var firstJson = JsonDocument.Parse(first);
            using var secondJson = JsonDocument.Parse(second);
            foreach (var field in new[] { "salt", "nonce", "ciphertext" })
                Assert.NotEqual(firstJson.RootElement.GetProperty(field).GetString(), secondJson.RootElement.GetProperty(field).GetString());
        }
        finally { Zero(first); Zero(second); }
    }

    [Fact]
    public void Invalid_macro_is_counted_while_other_entries_are_imported()
    {
        var valid = Document("synthetic valid", []);
        var invalid = Document("synthetic invalid", [new MacroAction { Type = MacroActionType.MouseButtonDown, Button = "synthetic-invalid-button" }]);
        byte[]? file = null;
        try
        {
            file = CreateExportFile([valid, invalid]);
            var result = FastService().Import(file, TestPassphrase);
            Assert.Single(result.Macros);
            Assert.Equal(1, result.RejectedCount);
            Assert.Equal(1, result.InvalidCount);
            Assert.Equal(valid.Id, result.Macros[0].Id);
        }
        finally { Zero(file); }
    }

    private static MacroDocument Document(string name, List<MacroAction> actions) => new()
    {
        FormatVersion = MacroStorage.PayloadFormatVersion,
        Name = name,
        Actions = actions
    };

    private static byte[] CreateExportFile(IReadOnlyList<MacroDocument> macros)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TestPayload { ExportFormat = 1, Macros = macros }, JsonOptions);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = new byte[32];
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        var passphrase = TestPassphrase.AsSpan();
        byte[]? associatedData = null;
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, 100, HashAlgorithmName.SHA256);
            var header = new TestHeader
            {
                ExportFormat = 1,
                Kdf = "pbkdf2-sha256",
                Iterations = 100,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Cipher = "aes-256-gcm"
            };
            associatedData = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, payload, ciphertext, tag, associatedData);
            return JsonSerializer.SerializeToUtf8Bytes(new TestEnvelope
            {
                ExportFormat = header.ExportFormat,
                Kdf = header.Kdf,
                Iterations = header.Iterations,
                Salt = header.Salt,
                Nonce = header.Nonce,
                Cipher = header.Cipher,
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            }, JsonOptions);
        }
        finally
        {
            Zero(payload); Zero(salt); Zero(nonce); Zero(key); Zero(ciphertext); Zero(tag); Zero(associatedData);
        }
    }

    private static byte[] MutateField(byte[] file, string field)
    {
        using var json = JsonDocument.Parse(file);
        var value = json.RootElement.GetProperty(field);
        if (field == "iterations") return SetField(file, field, JsonValue.Create(value.GetInt32() + 1));
        var bytes = Convert.FromBase64String(value.GetString()!);
        bytes[0] ^= 0x01;
        var changed = SetField(file, field, JsonValue.Create(Convert.ToBase64String(bytes)));
        Zero(bytes);
        return changed;
    }

    private static byte[] SetField(byte[] file, string field, JsonNode? value)
    {
        var root = JsonNode.Parse(file)!.AsObject();
        root[field] = value?.DeepClone();
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static void AssertDocumentsEqual(MacroDocument expected, MacroDocument actual)
    {
        Assert.True(expected.FormatVersion == actual.FormatVersion, "Macro version differed.");
        Assert.True(expected.Id == actual.Id, "Macro identifier differed.");
        Assert.True(expected.Name == actual.Name, "Macro name differed.");
        Assert.True(expected.Application == actual.Application, "Application field differed.");
        Assert.True(expected.CreatedUtc == actual.CreatedUtc, "Creation timestamp differed.");
        Assert.True(expected.UpdatedUtc == actual.UpdatedUtc, "Update timestamp differed.");
        Assert.Equal(expected.Actions.Count, actual.Actions.Count);
        for (var index = 0; index < expected.Actions.Count; index++)
        {
            var left = expected.Actions[index];
            var right = actual.Actions[index];
            Assert.Equal(left.AtMs, right.AtMs);
            Assert.Equal(left.Type, right.Type);
            Assert.Equal(left.X, right.X);
            Assert.Equal(left.Y, right.Y);
            Assert.Equal(left.KeyCode, right.KeyCode);
            Assert.Equal(left.ScanCode, right.ScanCode);
            Assert.Equal(left.RawFlags, right.RawFlags);
            Assert.Equal(left.WheelDelta, right.WheelDelta);
            Assert.Equal(left.Button, right.Button);
        }
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
    private static void ZeroChars(char[] chars) => CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));

    private sealed class TestPayload
    {
        public int ExportFormat { get; set; }
        public IReadOnlyList<MacroDocument> Macros { get; set; } = [];
    }
    private class TestHeader
    {
        public int ExportFormat { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Cipher { get; set; } = "";
    }
    private sealed class TestEnvelope : TestHeader
    {
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }
}
