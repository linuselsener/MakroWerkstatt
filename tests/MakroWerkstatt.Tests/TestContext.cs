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

    public TestContext()
    {
        Storage = new MacroStorage(Path.Combine(RootPath, "appdata"));
    }

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
    public List<(string Message, string Title, DialogKind Kind)> Messages { get; } = [];

    public bool ConfirmRecordingConsent() => RecordingConsent;
    public bool ConfirmDiscardChanges() => DiscardChanges;
    public bool ConfirmDeleteMacro(string name) => DeleteMacro;
    public ClosePromptChoice ConfirmCloseWithUnsavedChanges() => ClosePromptChoice.Discard;
    public void Show(string message, string title, DialogKind kind) => Messages.Add((message, title, kind));
}
