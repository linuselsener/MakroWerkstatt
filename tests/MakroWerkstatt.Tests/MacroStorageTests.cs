using System.Text.Json;
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
        const string invalidValue = "invented-private-token";
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
    public void Save_writes_current_format_version_and_loads_v2_example()
    {
        using var context = new TestContext();
        var document = DocumentWithAction(MacroActionType.KeyDown, button: "");
        document.FormatVersion = 1;
        context.Storage.Save(document);
        var savedText = File.ReadAllText(Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json"));
        Assert.Contains("\"formatVersion\": 2", savedText, StringComparison.Ordinal);
        Assert.Equal(MacroStorage.CurrentFormatVersion, context.Storage.LoadById(document.Id).FormatVersion);

        var examplePath = Path.Combine(AppContext.BaseDirectory, "TestData", "makro-format-v2.example.json");
        using var exampleJson = JsonDocument.Parse(File.ReadAllText(examplePath));
        var exampleId = exampleJson.RootElement.GetProperty("id").GetGuid();
        File.Copy(examplePath, Path.Combine(context.Storage.MacroFolder, $"{exampleId:N}.json"));

        var example = context.Storage.LoadById(exampleId);
        Assert.Equal(MacroStorage.CurrentFormatVersion, example.FormatVersion);
        Assert.Equal("Beispiel: relative Mausbewegung", example.Name);
        Assert.Contains(example.Actions, action => action.Type == MacroActionType.MouseMoveRelative);
    }

    [Fact]
    public void Settings_are_saved_and_loaded_inside_the_supplied_root()
    {
        using var context = new TestContext();
        var settings = new MacroSettings { RepeatCount = "3", RelativeMouseMovement = true };

        context.Storage.SaveSettings(settings);
        var loaded = context.Storage.LoadSettings();

        Assert.Equal(Path.Combine(context.Storage.RootPath, "settings.json"), context.Storage.SettingsPath);
        Assert.Equal("3", loaded.RepeatCount);
        Assert.True(loaded.RelativeMouseMovement);
    }

    private static MacroDocument DocumentWithAction(MacroActionType type, string button, long atMs = 0) => new()
    {
        Actions = [new MacroAction { AtMs = atMs, Type = type, Button = button }]
    };
}
