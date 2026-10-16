using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class MainViewModelTests
{
    [Fact]
    public void Recording_commands_follow_state_transitions_and_ignore_invalid_transitions()
    {
        using var context = new TestContext();
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService())
        {
            InputHookAvailable = true
        };

        Assert.Equal(RecordingState.Idle, viewModel.RecordingState);
        Assert.True(viewModel.StartRecordingCommand.CanExecute(null));
        Assert.False(viewModel.ToggleRecordingPauseCommand.CanExecute(null));
        Assert.False(viewModel.StopRecordingCommand.CanExecute(null));
        Assert.False(viewModel.DiscardRecordingCommand.CanExecute(null));

        viewModel.ToggleRecordingPauseCommand.Execute(null);
        viewModel.StopRecordingCommand.Execute(null);
        viewModel.DiscardRecordingCommand.Execute(null);
        Assert.Equal(RecordingState.Idle, viewModel.RecordingState);

        viewModel.StartRecordingCommand.Execute(null);
        Assert.Equal(RecordingState.Recording, viewModel.RecordingState);
        Assert.False(viewModel.StartRecordingCommand.CanExecute(null));
        Assert.True(viewModel.ToggleRecordingPauseCommand.CanExecute(null));
        Assert.True(viewModel.StopRecordingCommand.CanExecute(null));
        Assert.True(viewModel.DiscardRecordingCommand.CanExecute(null));
        viewModel.CaptureAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 65 });

        viewModel.ToggleRecordingPauseCommand.Execute(null);
        Assert.Equal(RecordingState.Paused, viewModel.RecordingState);
        Assert.False(viewModel.StartRecordingCommand.CanExecute(null));
        Assert.True(viewModel.ToggleRecordingPauseCommand.CanExecute(null));
        Assert.True(viewModel.StopRecordingCommand.CanExecute(null));
        Assert.True(viewModel.DiscardRecordingCommand.CanExecute(null));
        viewModel.ToggleRecordingPauseCommand.Execute(null);

        Assert.Equal(RecordingState.Recording, viewModel.RecordingState);
        viewModel.StopRecordingCommand.Execute(null);
        Assert.Equal(RecordingState.Idle, viewModel.RecordingState);
        Assert.Single(viewModel.Actions);
        Assert.False(viewModel.ToggleRecordingPauseCommand.CanExecute(null));
        Assert.False(viewModel.StopRecordingCommand.CanExecute(null));
        Assert.False(viewModel.DiscardRecordingCommand.CanExecute(null));

        viewModel.StartRecordingCommand.Execute(null);
        viewModel.CaptureAction(new MacroAction { Type = MacroActionType.KeyUp, KeyCode = 65 });
        viewModel.ToggleRecordingPauseCommand.Execute(null);
        viewModel.DiscardRecordingCommand.Execute(null);
        Assert.Equal(RecordingState.Idle, viewModel.RecordingState);
        Assert.Empty(viewModel.Actions);
        Assert.Null(viewModel.CurrentMacro);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void Removing_selected_action_updates_selection_actions_and_dirty_state()
    {
        using var context = new TestContext();
        var macro = new MacroDocument
        {
            Name = "Synthetic action list",
            Actions =
            [
                new MacroAction { AtMs = 0, Type = MacroActionType.KeyDown, KeyCode = 65 },
                new MacroAction { AtMs = 10, Type = MacroActionType.KeyUp, KeyCode = 65 }
            ]
        };
        context.Storage.Save(macro);

        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService());
        viewModel.LoadLibrary(showWarnings: false);
        viewModel.SelectedLibraryItem = Assert.Single(viewModel.Library);
        var removed = viewModel.Actions[0];
        viewModel.SelectedAction = removed;

        Assert.False(viewModel.IsDirty);
        Assert.True(viewModel.RemoveActionCommand.CanExecute(null));
        viewModel.RemoveActionCommand.Execute(null);

        Assert.Single(viewModel.Actions);
        Assert.Equal(10, viewModel.Actions[0].AtMs);
        Assert.Equal(MacroActionType.KeyUp, viewModel.Actions[0].Type);
        Assert.Single(viewModel.CurrentMacro!.Actions);
        Assert.Null(viewModel.SelectedAction);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.SaveMacroCommand.CanExecute(null));
    }

    [Fact]
    public void Library_can_save_load_rename_and_delete_a_macro_in_storage()
    {
        using var context = new TestContext();
        var dialogs = new FakeDialogService();
        using var viewModel = new MainViewModel(context.Storage, dialogs) { InputHookAvailable = true };

        viewModel.StartRecordingCommand.Execute(null);
        viewModel.CaptureAction(new MacroAction { Type = MacroActionType.MouseMove, X = 12, Y = 34 });
        viewModel.StopRecordingCommand.Execute(null);
        viewModel.MacroName = "Synthetic macro";

        Assert.True(viewModel.SaveMacroCommand.CanExecute(null));
        viewModel.SaveMacroCommand.Execute(null);
        var saved = context.Storage.LoadById(viewModel.CurrentMacro!.Id);
        Assert.Equal("Synthetic macro", saved.Name);
        Assert.Equal(MacroStorage.PayloadFormatVersion, saved.FormatVersion);
        Assert.True(File.Exists(Path.Combine(context.Storage.MacroFolder, $"{saved.Id:N}.json")));

        viewModel.LoadLibrary(showWarnings: false);
        viewModel.SelectedLibraryItem = Assert.Single(viewModel.Library);
        Assert.Equal("Synthetic macro", viewModel.MacroName);
        Assert.False(viewModel.IsDirty);

        viewModel.MacroName = "Renamed synthetic macro";
        Assert.True(viewModel.IsDirty);
        viewModel.SaveMacroCommand.Execute(null);
        Assert.Equal("Renamed synthetic macro", context.Storage.LoadById(saved.Id).Name);

        Assert.True(viewModel.DeleteMacroCommand.CanExecute(null));
        viewModel.DeleteMacroCommand.Execute(null);
        Assert.False(File.Exists(Path.Combine(context.Storage.MacroFolder, $"{saved.Id:N}.json")));
        Assert.Empty(viewModel.Library);
        Assert.Null(viewModel.CurrentMacro);
    }

    [Fact]
    public void Encryption_setting_is_persisted_and_existing_macro_migration_requires_dialog_confirmation()
    {
        using var context = new TestContext();
        var dialogs = new FakeDialogService();
        using var viewModel = new MainViewModel(context.Storage, dialogs);

        viewModel.LoadSettings();
        Assert.True(viewModel.EncryptMacros);
        viewModel.EncryptMacros = false;
        viewModel.SaveSettings(showFailure: true);
        Assert.False(context.Storage.LoadSettings().EncryptMacros);

        var document = new MacroDocument
        {
            Name = "Synthetic plaintext macro",
            Actions = [new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 65 }]
        };
        context.WriteFixture(document);
        viewModel.EncryptExistingMacrosCommand.Execute(null);

        Assert.Equal(1, dialogs.ConfirmedMigrationCount);
        Assert.Contains("1 erfolgreich, 0 fehlgeschlagen", viewModel.FooterMessage, StringComparison.Ordinal);
        using var savedJson = System.Text.Json.JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(context.Storage.MacroFolder, $"{document.Id:N}.json")));
        Assert.Equal(MacroStorage.CurrentFormatVersion, savedJson.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void Hotkey_capture_validates_function_keys_and_rejects_conflicts()
    {
        using var context = new TestContext();
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService());
        var originalRecordingHotkey = viewModel.RecordingHotkey;

        Assert.False(viewModel.TrySetHotkey(forRecording: true, virtualKey: 0x41, HotkeyModifiers.None));
        Assert.Equal(originalRecordingHotkey, viewModel.RecordingHotkey);
        Assert.Equal("Bitte F1 bis F12 drücken, optional mit Ctrl, Alt oder Shift", viewModel.FooterMessage);

        Assert.False(viewModel.TrySetHotkey(forRecording: true,
            viewModel.PlaybackHotkey.VirtualKey, viewModel.PlaybackHotkey.Modifiers));
        Assert.Equal(originalRecordingHotkey, viewModel.RecordingHotkey);

        Assert.True(viewModel.TrySetHotkey(forRecording: true, virtualKey: 0x70,
            HotkeyModifiers.Control | HotkeyModifiers.Shift));
        Assert.Equal(new GlobalHotkey(0x70, HotkeyModifiers.Control | HotkeyModifiers.Shift),
            viewModel.RecordingHotkey);
    }
}
