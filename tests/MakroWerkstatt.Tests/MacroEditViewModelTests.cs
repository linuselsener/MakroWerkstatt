using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class MacroEditViewModelTests
{
    [Fact]
    public async Task Editor_commands_are_enabled_in_idle_and_disabled_while_recording_paused_or_playing()
    {
        using var context = new TestContext();
        var macro = Fixture(
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove, X = 1, Y = 2 },
            new MacroAction { AtMs = 10, Type = MacroActionType.MouseMove, X = 3, Y = 4 },
            new MacroAction { AtMs = 20, Type = MacroActionType.MouseWheel, WheelDelta = 120 });
        context.Storage.Save(macro, encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService()) { InputHookAvailable = true };
        Load(viewModel);
        Assert.False(viewModel.MoveSelectionToIndexCommand.CanExecute(0));
        viewModel.SelectedAction = viewModel.Actions[1];

        Assert.True(viewModel.RemoveActionCommand.CanExecute(null));
        Assert.True(viewModel.MoveActionUpCommand.CanExecute(null));
        Assert.True(viewModel.MoveActionDownCommand.CanExecute(null));
        Assert.True(viewModel.MoveSelectionToIndexCommand.CanExecute(0));
        Assert.False(viewModel.MoveSelectionToIndexCommand.CanExecute(1));
        Assert.True(viewModel.DuplicateActionsCommand.CanExecute(null));
        Assert.True(viewModel.InsertPauseCommand.CanExecute(null));
        Assert.True(viewModel.ApplyActionEditsCommand.CanExecute(null));
        Assert.False(viewModel.UndoActionEditCommand.CanExecute(null));
        Assert.False(viewModel.RedoActionEditCommand.CanExecute(null));

        viewModel.StartRecordingCommand.Execute(null);
        Assert.Equal(RecordingState.Recording, viewModel.RecordingState);
        AssertEditorCommandsDisabled(viewModel);
        viewModel.ToggleRecordingPauseCommand.Execute(null);
        Assert.Equal(RecordingState.Paused, viewModel.RecordingState);
        AssertEditorCommandsDisabled(viewModel);
        viewModel.StopRecordingCommand.Execute(null);

        using var playbackViewModel = new MainViewModel(context.Storage, new FakeDialogService()) { InputHookAvailable = true };
        Load(playbackViewModel);
        playbackViewModel.SelectedAction = playbackViewModel.Actions[0];
        playbackViewModel.PlayCommand.Execute(null);
        Assert.True(playbackViewModel.IsPlaying);
        AssertEditorCommandsDisabled(playbackViewModel);
        playbackViewModel.StopPlaybackCommand.Execute(null);
        await WaitUntil(() => !playbackViewModel.IsPlaying);
    }

    [Fact]
    public void View_model_edit_undo_and_redo_restore_dirty_state_and_values()
    {
        using var context = new TestContext();
        context.Storage.Save(Fixture(
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove, X = 1, Y = 2 },
            new MacroAction { AtMs = 10, Type = MacroActionType.MouseMove, X = 3, Y = 4 }), encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService());
        Load(viewModel);
        viewModel.SelectedAction = viewModel.Actions[1];
        viewModel.EditDelayMs = "5";
        viewModel.EditX = "42";
        viewModel.EditY = "-7";

        viewModel.ApplyActionEditsCommand.Execute(null);
        Assert.Equal(5, viewModel.Actions[1].AtMs);
        Assert.True(viewModel.Actions[1].X == 42);
        Assert.True(viewModel.Actions[1].Y == -7);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.UndoActionEditCommand.CanExecute(null));

        viewModel.UndoActionEditCommand.Execute(null);
        Assert.Equal(10, viewModel.Actions[1].AtMs);
        Assert.True(viewModel.Actions[1].X == 3);
        Assert.False(viewModel.IsDirty);
        viewModel.RedoActionEditCommand.Execute(null);
        Assert.Equal(5, viewModel.Actions[1].AtMs);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void Invalid_editor_time_is_rejected_with_a_short_german_message_and_no_change()
    {
        using var context = new TestContext();
        var dialogs = new FakeDialogService();
        context.Storage.Save(Fixture(new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove, X = 1, Y = 2 }), encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, dialogs);
        Load(viewModel);
        viewModel.SelectedAction = viewModel.Actions[0];
        viewModel.EditDelayMs = "-1";

        viewModel.ApplyActionEditsCommand.Execute(null);

        Assert.Equal(0, viewModel.Actions[0].AtMs);
        Assert.True(viewModel.Actions[0].X == 1);
        Assert.False(viewModel.IsDirty);
        Assert.Contains("Abstand", Assert.Single(dialogs.Messages).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_selection_can_delete_actions_and_keeps_a_valid_next_selection()
    {
        using var context = new TestContext();
        context.Storage.Save(Fixture(
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove, X = 1, Y = 2 },
            new MacroAction { AtMs = 10, Type = MacroActionType.MouseWheel, WheelDelta = 120 },
            new MacroAction { AtMs = 20, Type = MacroActionType.MouseMoveRelative, X = -3, Y = 4 }), encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService());
        Load(viewModel);
        viewModel.SetSelectedActions([viewModel.Actions[0], viewModel.Actions[2]]);

        Assert.True(viewModel.RemoveActionCommand.CanExecute(null));
        viewModel.RemoveActionCommand.Execute(null);

        Assert.Single(viewModel.Actions);
        Assert.True(viewModel.Actions[0].Type == MacroActionType.MouseWheel);
        Assert.Same(viewModel.Actions[0], viewModel.SelectedAction);
        Assert.True(viewModel.IsDirty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Edited_macro_saves_and_reloads_in_both_storage_modes(bool encrypt)
    {
        using var context = new TestContext();
        var macro = Fixture(
            new MacroAction { AtMs = 0, Type = MacroActionType.KeyDown, KeyCode = 65, ScanCode = 30 },
            new MacroAction { AtMs = 10, Type = MacroActionType.KeyUp, KeyCode = 65, ScanCode = 30, RawFlags = 0x80 });
        context.Storage.Save(macro, encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService());
        Load(viewModel);
        viewModel.SelectedAction = viewModel.Actions[0];

        Assert.True(viewModel.TrySetSelectedKey(66));
        viewModel.EncryptMacros = encrypt;
        Assert.True(viewModel.SaveCurrentMacro(showSuccess: false));

        var reopened = context.Storage.LoadById(macro.Id);
        MacroStorage.ValidateDocument(reopened);
        Assert.True(reopened.Actions[0].KeyCode == 66);
        Assert.True(reopened.Actions[1].KeyCode == 66);
    }

    [Fact]
    public void Loading_another_macro_and_starting_a_new_recording_reset_edit_history()
    {
        using var context = new TestContext();
        var first = Fixture(new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove, X = 1, Y = 2 });
        var second = Fixture(new MacroAction { AtMs = 0, Type = MacroActionType.MouseWheel, WheelDelta = 120 });
        context.Storage.Save(first, encrypt: false);
        context.Storage.Save(second, encrypt: false);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService()) { InputHookAvailable = true };
        viewModel.LoadLibrary(showWarnings: false);
        viewModel.SelectedLibraryItem = viewModel.Library.Single(item => item.Id == first.Id);
        viewModel.SelectedAction = viewModel.Actions[0];
        viewModel.EditX = "8";
        viewModel.ApplyActionEditsCommand.Execute(null);
        Assert.True(viewModel.UndoActionEditCommand.CanExecute(null));

        viewModel.SelectedLibraryItem = viewModel.Library.Single(item => item.Id == second.Id);
        Assert.False(viewModel.UndoActionEditCommand.CanExecute(null));
        Assert.False(viewModel.RedoActionEditCommand.CanExecute(null));

        viewModel.SelectedAction = viewModel.Actions[0];
        viewModel.EditWheelDelta = "240";
        viewModel.ApplyActionEditsCommand.Execute(null);
        Assert.True(viewModel.UndoActionEditCommand.CanExecute(null));
        viewModel.StartRecordingCommand.Execute(null);
        Assert.False(viewModel.UndoActionEditCommand.CanExecute(null));
        Assert.False(viewModel.RedoActionEditCommand.CanExecute(null));
        viewModel.DiscardRecordingCommand.Execute(null);
    }

    private static void Load(MainViewModel viewModel)
    {
        viewModel.LoadLibrary(showWarnings: false);
        viewModel.SelectedLibraryItem = Assert.Single(viewModel.Library);
    }

    private static MacroDocument Fixture(params MacroAction[] actions) => new()
    {
        Name = "Synthetic editor fixture",
        Actions = actions.ToList()
    };

    private static void AssertEditorCommandsDisabled(MainViewModel viewModel)
    {
        Assert.False(viewModel.RemoveActionCommand.CanExecute(null));
        Assert.False(viewModel.MoveActionUpCommand.CanExecute(null));
        Assert.False(viewModel.MoveActionDownCommand.CanExecute(null));
        Assert.False(viewModel.MoveSelectionToIndexCommand.CanExecute(0));
        Assert.False(viewModel.DuplicateActionsCommand.CanExecute(null));
        Assert.False(viewModel.InsertPauseCommand.CanExecute(null));
        Assert.False(viewModel.UndoActionEditCommand.CanExecute(null));
        Assert.False(viewModel.RedoActionEditCommand.CanExecute(null));
        Assert.False(viewModel.ApplyActionEditsCommand.CanExecute(null));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }
}
