using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class MacroStorageTests
{
    [Theory]
    [InlineData(MacroMouseButtons.Left)]
    [InlineData(MacroMouseButtons.Right)]
    [InlineData(MacroMouseButtons.Middle)]
    [InlineData(MacroMouseButtons.X1)]
    [InlineData(MacroMouseButtons.X2)]
    public void Load_accepts_each_allowed_mouse_button(string button)
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.MouseButtonDown, button);
        context.WriteFixture(document);

        var loaded = context.Storage.LoadById(document.Id);

        Assert.Equal(button, Assert.Single(loaded.Actions).Button);
    }

    [Fact]
    public void Load_rejects_unknown_mouse_button_without_echoing_raw_value()
    {
        using var context = new TestContext();
        const string invalidValue = "private-token";
        var document = DocumentWithAction(MacroActionType.MouseButtonUp, invalidValue);
        context.WriteFixture(document);

        var error = Assert.Throws<InvalidDataException>(() => context.Storage.LoadById(document.Id));

        Assert.Contains("Aktion 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("unbekannte Maustaste", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidValue, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_accepts_version_one_documents()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.MouseButtonDown, MacroMouseButtons.Left);
        document.FormatVersion = 1;
        context.WriteFixture(document);

        var loaded = context.Storage.LoadById(document.Id);

        Assert.Equal(1, loaded.FormatVersion);
        Assert.Equal(MacroMouseButtons.Left, Assert.Single(loaded.Actions).Button);
    }

    [Fact]
    public void Load_accepts_empty_action_list()
    {
        using var context = new TestContext();
        var document = new MacroDocument { Actions = [] };
        context.WriteFixture(document);

        Assert.Empty(context.Storage.LoadById(document.Id).Actions);
    }

    [Fact]
    public void Load_rejects_negative_timestamp()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, button: "", atMs: -1);
        context.WriteFixture(document);

        Assert.Throws<InvalidDataException>(() => context.Storage.LoadById(document.Id));
    }

    [Fact]
    public void Load_does_not_validate_button_field_on_non_mouse_action()
    {
        using var context = new TestContext();
        const string unrelatedButton = "invented-x";
        var document = DocumentWithAction(MacroActionType.KeyDown, unrelatedButton);
        context.WriteFixture(document);

        var loaded = context.Storage.LoadById(document.Id);

        Assert.Equal(unrelatedButton, Assert.Single(loaded.Actions).Button);
    }

    [Fact]
    public void Save_writes_an_encrypted_v3_envelope_and_loads_the_same_document()
    {
        using var context = new TestContext();
        const string marker = "secret-marker-42";
        var document = DocumentWithAction(MacroActionType.KeyDown, marker);
        document.Name = $"Name-{marker}";

        context.Storage.Save(document);

        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");
        var bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(MacroStorage.CurrentFormatVersion, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("dpapi-currentuser", json.RootElement.GetProperty("encryption").GetString());
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));

        var loaded = context.Storage.LoadById(document.Id);
        Assert.Equal(MacroStorage.PayloadFormatVersion, loaded.FormatVersion);
        Assert.Equal(document.Id, loaded.Id);
        Assert.Equal(document.Name, loaded.Name);
        Assert.Equal(marker, Assert.Single(loaded.Actions).Button);
    }

    [Fact]
    public void Load_reads_version_two_example_without_migrating_it()
    {
        using var context = new TestContext();
        var examplePath = Path.Combine(AppContext.BaseDirectory, "TestData", "makro-format-v2.example.json");
        using var exampleJson = JsonDocument.Parse(File.ReadAllText(examplePath));
        var exampleId = exampleJson.RootElement.GetProperty("id").GetGuid();
        var destination = Path.Combine(context.Storage.MacroFolder, $"{exampleId:N}.json");
        File.Copy(examplePath, destination);
        var before = File.ReadAllBytes(destination);

        var example = context.Storage.LoadById(exampleId);

        Assert.Equal(MacroStorage.PayloadFormatVersion, example.FormatVersion);
        Assert.Equal("Beispiel: relative Mausbewegung", example.Name);
        Assert.Contains(example.Actions, action => action.Type == MacroActionType.MouseMoveRelative);
        Assert.Equal(before, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Save_can_write_plaintext_version_two_when_encryption_is_disabled()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, button: "");

        context.Storage.Save(document, encrypt: false);

        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(MacroStorage.PayloadFormatVersion, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(document.Name, context.Storage.LoadById(document.Id).Name);
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));
    }

    [Theory]
    [InlineData("{\"formatVersion\":3,\"encryption\":\"dpapi-currentuser\",\"payload\":\"not-base64-private-marker\"}")]
    [InlineData("{\"formatVersion\":3,\"encryption\":\"private-encryption-marker\",\"payload\":\"YQ==\"}")]
    [InlineData("{\"formatVersion\":3,\"encryption\":\"dpapi-currentuser\"}")]
    [InlineData("{\"formatVersion\":4,\"name\":\"private-future-marker\"}")]
    public void Load_rejects_invalid_envelopes_and_future_versions_without_exposing_contents(string jsonText)
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        context.WriteRaw(id, jsonText);

        var error = Assert.Throws<InvalidDataException>(() => context.Storage.LoadById(id));

        Assert.DoesNotContain("private-", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_skips_a_macro_that_cannot_be_decrypted_and_loads_the_rest()
    {
        using var context = new TestContext();
        const string privateMarker = "private-secret";
        var encrypted = DocumentWithAction(MacroActionType.KeyDown, privateMarker);
        context.Storage.Save(encrypted);
        var plaintext = DocumentWithAction(MacroActionType.KeyDown, "");
        plaintext.Name = "Valid legacy macro";
        context.WriteFixture(plaintext);
        context.Protector.FailUnprotect = true;

        var loaded = context.Storage.LoadAll(out var warnings);

        Assert.Equal(plaintext.Id, Assert.Single(loaded).Id);
        Assert.Single(warnings);
        Assert.Contains("anderes Windows-Benutzerkonto", warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(privateMarker, warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_encrypts_plaintext_files_and_leaves_no_temporary_file()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, "synthetic");
        context.WriteFixture(document);
        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");

        Assert.Equal(1, context.Storage.CountPlaintextMacros());
        var result = context.Storage.EncryptExistingPlaintextMacros();

        Assert.Equal(new MacroMigrationResult(1, 0), result);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(MacroStorage.CurrentFormatVersion, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("synthetic", Assert.Single(context.Storage.LoadById(document.Id).Actions).Button);
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));
    }

    [Fact]
    public void Migration_keeps_original_unchanged_when_atomic_replace_fails()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, "synthetic");
        context.WriteFixture(document);
        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");
        var original = File.ReadAllBytes(path);
        var storage = context.CreateStorage(new ThrowOnReplaceOperations());

        var result = storage.EncryptExistingPlaintextMacros();

        Assert.Equal(new MacroMigrationResult(0, 1), result);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));
    }

    [Fact]
    public void Migration_keeps_original_unchanged_when_readback_decryption_fails()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, "synthetic");
        context.WriteFixture(document);
        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");
        var original = File.ReadAllBytes(path);
        context.Protector.FailUnprotect = true;

        var result = context.Storage.EncryptExistingPlaintextMacros();

        Assert.Equal(new MacroMigrationResult(0, 1), result);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));
    }

    [Fact]
    public void Save_keeps_existing_file_and_removes_temporary_file_when_replace_fails()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, "before");
        context.Storage.Save(document);
        var path = Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json");
        var original = File.ReadAllBytes(path);
        document.Name = "after";
        var storage = context.CreateStorage(new ThrowOnReplaceOperations());

        Assert.Throws<IOException>(() => storage.Save(document));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(context.Storage.MacroFolder, "*.tmp"));
    }

    [Fact]
    public void Settings_default_to_encryption_and_persist_the_choice_inside_the_supplied_root()
    {
        using var context = new TestContext();
        Assert.True(context.Storage.LoadSettings().EncryptMacros);
        var settings = new MacroSettings { RepeatCount = "3", RelativeMouseMovement = true, EncryptMacros = false };

        context.Storage.SaveSettings(settings);
        var loaded = context.Storage.LoadSettings();

        Assert.Equal(Path.Combine(context.Storage.RootPath, "settings.json"), context.Storage.SettingsPath);
        Assert.Equal("3", loaded.RepeatCount);
        Assert.True(loaded.RelativeMouseMovement);
        Assert.False(loaded.EncryptMacros);
    }

    [Fact]
    public void Dpapi_protect_and_unprotect_roundtrip_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiDataProtector();
        var plaintext = Encoding.UTF8.GetBytes("synthetic DPAPI roundtrip");
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = protector.Protect(plaintext);
            var recovered = protector.Unprotect(protectedBytes);
            try
            {
                Assert.Equal(plaintext, recovered);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recovered);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static MacroDocument DocumentWithAction(MacroActionType type, string button, long atMs = 0) => new()
    {
        Actions = [new MacroAction { AtMs = atMs, Type = type, Button = button }]
    };

    private sealed class ThrowOnReplaceOperations : IMacroFileOperations
    {
        private readonly IMacroFileOperations _inner = PhysicalMacroFileOperations.Instance;

        public void WriteAllBytes(string path, byte[] content) => _inner.WriteAllBytes(path, content);
        public void Replace(string temporaryPath, string destinationPath) => throw new IOException("synthetic replace failure");
        public void Move(string temporaryPath, string destinationPath) => _inner.Move(temporaryPath, destinationPath);
        public void TryDelete(string path) => _inner.TryDelete(path);
    }
}
