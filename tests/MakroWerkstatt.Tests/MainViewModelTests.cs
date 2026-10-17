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

    [Fact]
    public void Export_all_uses_file_and_passphrase_services_and_clears_the_passphrase_buffer()
    {
        using var context = new TestContext();
        context.Storage.Save(new MacroDocument { Name = "synthetic export source", Actions = [] });
        var dialogs = new FakeDialogService();
        var fileDialogs = new FakeFileDialogService { SavePath = Path.Combine(context.RootPath, "backup.mwexport") };
        var passphraseDialogs = new FakePassphraseDialogService();
        var service = new MacroExportService(100, 10);
        using var viewModel = new MainViewModel(context.Storage, dialogs, fileDialogs: fileDialogs,
            passphraseDialogs: passphraseDialogs, exportService: service);
        viewModel.LoadLibrary(showWarnings: false);

        viewModel.ExportAllMacrosCommand.Execute(null);

        Assert.True(File.Exists(fileDialogs.SavePath), "The encrypted export file was not written.");
        Assert.True(passphraseDialogs.LastConfirmOption == true, "The export passphrase prompt did not request confirmation.");
        Assert.Equal(1, fileDialogs.SaveRequests);
        Assert.Contains("1 exportiert, 0 übersprungen", viewModel.FooterMessage, StringComparison.Ordinal);
        Assert.True(passphraseDialogs.LastReturnedBuffer!.All(character => character == '\0'),
            "The passphrase buffer was not cleared after export.");

        var bytes = File.ReadAllBytes(fileDialogs.SavePath!);
        var passphrase = "synthetic passphrase for tests".ToCharArray();
        try
        {
            var result = service.Import(bytes, passphrase);
            Assert.Single(result.Macros);
            Assert.True(result.Macros[0].Name == "synthetic export source", "The exported document name did not roundtrip.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
        }
    }

    [Fact]
    public void Export_rejects_short_passphrase_at_view_model_boundary_before_file_selection()
    {
        using var context = new TestContext();
        context.Storage.Save(new MacroDocument { Name = "synthetic short passphrase source", Actions = [] });
        var dialogs = new FakeDialogService();
        var fileDialogs = new FakeFileDialogService { SavePath = Path.Combine(context.RootPath, "unused.mwexport") };
        var passphraseDialogs = new FakePassphraseDialogService { Value = "short" };
        using var viewModel = new MainViewModel(context.Storage, dialogs, fileDialogs: fileDialogs,
            passphraseDialogs: passphraseDialogs, exportService: new MacroExportService(100, 10));
        viewModel.LoadLibrary(showWarnings: false);

        viewModel.ExportAllMacrosCommand.Execute(null);

        Assert.Equal(0, fileDialogs.SaveRequests);
        Assert.False(File.Exists(fileDialogs.SavePath));
        Assert.Contains(dialogs.Messages, message => message.Title == "Passphrase zu kurz");
        Assert.True(passphraseDialogs.LastReturnedBuffer!.All(character => character == '\0'),
            "The short passphrase buffer was not cleared.");
    }

    [Fact]
    public void Export_all_reports_unreadable_storage_entries_and_exports_readable_macros()
    {
        using var context = new TestContext();
        context.WriteFixture(new MacroDocument { Name = "synthetic readable", Actions = [] });
        context.Storage.Save(new MacroDocument { Name = "synthetic unreadable", Actions = [] });
        context.Protector.FailUnprotect = true;
        var dialogs = new FakeDialogService();
        var fileDialogs = new FakeFileDialogService { SavePath = Path.Combine(context.RootPath, "partial.mwexport") };
        using var viewModel = new MainViewModel(context.Storage, dialogs, fileDialogs: fileDialogs,
            passphraseDialogs: new FakePassphraseDialogService(), exportService: new MacroExportService(100, 10));
        viewModel.LoadLibrary(showWarnings: false);

        viewModel.ExportAllMacrosCommand.Execute(null);

        Assert.True(File.Exists(fileDialogs.SavePath), "The readable macro was not exported.");
        Assert.Contains("1 exportiert, 1 übersprungen", viewModel.FooterMessage, StringComparison.Ordinal);
        Assert.True(!string.Join("\n", dialogs.Messages.Select(message => message.Message))
            .Contains("synthetic unreadable", StringComparison.Ordinal), "A macro name appeared in a dialog.");
    }
}
