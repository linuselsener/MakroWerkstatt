using System.Text.Json;
using System.Text.Json.Serialization;
using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[CollectionDefinition("SerialTests", DisableParallelization = true)]
public sealed class SerialTestsCollection { }

internal sealed class TestContext : IDisposable
{
    public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "MakroWerkstatt.Tests", Guid.NewGuid().ToString("N"));
    public MacroStorage Storage { get; }
    public DeterministicFakeProtector Protector { get; } = new();

    public TestContext()
    {
        Storage = new MacroStorage(Path.Combine(RootPath, "appdata"), Protector);
    }

    public MacroStorage CreateStorage(IMacroFileOperations? fileOperations = null) =>
        fileOperations is null
            ? new MacroStorage(Path.Combine(RootPath, "appdata"), Protector)
            : new MacroStorage(Path.Combine(RootPath, "appdata"), Protector, fileOperations);

    public void WriteFixture(MacroDocument document)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var path = Path.Combine(Storage.MacroFolder, $"{document.Id:N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, options));
    }

    public void WriteRaw(Guid id, string json) =>
        File.WriteAllText(Path.Combine(Storage.MacroFolder, $"{id:N}.json"), json);

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
            Directory.Delete(RootPath, recursive: true);
    }
}

internal sealed class FakeDialogService : IDialogService
{
    public bool RecordingConsent { get; set; } = true;
    public bool DiscardChanges { get; set; } = true;
    public bool DeleteMacro { get; set; } = true;
    public bool EncryptExistingMacros { get; set; } = true;
    public ImportCollisionAction ImportCollisionAction { get; set; } = ImportCollisionAction.Skip;
    public bool ImportOverwriteConfirmed { get; set; }
    public int? ConfirmedMigrationCount { get; private set; }
    public int ImportCollisionPromptCount { get; private set; }
    public int ImportOverwritePromptCount { get; private set; }
    public List<(string Message, string Title, DialogKind Kind)> Messages { get; } = [];

    public bool ConfirmRecordingConsent() => RecordingConsent;
    public bool ConfirmDiscardChanges() => DiscardChanges;
    public bool ConfirmDeleteMacro(string name) => DeleteMacro;
    public bool ConfirmEncryptExistingMacros(int macroCount)
    {
        ConfirmedMigrationCount = macroCount;
        return EncryptExistingMacros;
    }
    public ImportCollisionAction ChooseImportCollisionAction()
    {
        ImportCollisionPromptCount++;
        return ImportCollisionAction;
    }
    public bool ConfirmImportOverwrite()
    {
        ImportOverwritePromptCount++;
        return ImportOverwriteConfirmed;
    }
    public ClosePromptChoice ConfirmCloseWithUnsavedChanges() => ClosePromptChoice.Discard;
    public void Show(string message, string title, DialogKind kind) => Messages.Add((message, title, kind));
}

internal sealed class FakeFileDialogService : IFileDialogService
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public int SaveRequests { get; private set; }
    public int OpenRequests { get; private set; }

    public string? SaveMacroExport()
    {
        SaveRequests++;
        return SavePath;
    }

    public string? OpenMacroExport()
    {
        OpenRequests++;
        return OpenPath;
    }
}

internal sealed class FakePassphraseDialogService : IPassphraseDialogService
{
    public string? Value { get; set; } = "synthetic passphrase for tests";
    public int Requests { get; private set; }
    public bool? LastConfirmOption { get; private set; }
    public char[]? LastReturnedBuffer { get; private set; }

    public char[]? RequestPassphrase(bool confirm)
    {
        Requests++;
        LastConfirmOption = confirm;
        LastReturnedBuffer = Value?.ToCharArray();
        return LastReturnedBuffer;
    }
}

internal sealed class DeterministicFakeProtector : IDataProtector
{
    private static readonly byte[] Key = [0x37, 0xA1, 0x5C, 0xD2, 0x91, 0x08, 0x6F];

    public bool FailProtect { get; set; }
    public bool FailUnprotect { get; set; }

    public byte[] Protect(byte[] plaintext)
    {
        if (FailProtect) throw new MacroProtectionException();
        return Transform(plaintext);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (FailUnprotect) throw new MacroDecryptionException();
        return Transform(protectedData);
    }

    private static byte[] Transform(byte[] input)
    {
        var output = new byte[input.Length];
        for (var index = 0; index < input.Length; index++)
            output[index] = (byte)(input[index] ^ Key[index % Key.Length]);
        return output;
    }
}
