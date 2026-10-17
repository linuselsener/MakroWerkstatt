using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

public sealed class MacroEditServiceTests
{
    [Fact]
    public void Move_preserves_timestamp_slots_and_keeps_paired_inputs_together()
    {
        var service = CreateService(
            Move(0), KeyDown(10), KeyUp(20), Wheel(30));

        Assert.True(service.TryMove([1], moveUp: true));
        Assert.True(service.Actions.Select(action => action.Type).SequenceEqual(
            [MacroActionType.KeyDown, MacroActionType.KeyUp, MacroActionType.MouseMove, MacroActionType.MouseWheel]));
        Assert.True(service.Actions.Select(action => action.AtMs).SequenceEqual([0L, 10L, 20L, 30L]));
        AssertMonotonic(service.Actions);

        Assert.True(service.TryMove([1], moveUp: false));
        Assert.True(service.Actions.Select(action => action.Type).SequenceEqual(
            [MacroActionType.MouseMove, MacroActionType.KeyDown, MacroActionType.KeyUp, MacroActionType.MouseWheel]));
        AssertMonotonic(service.Actions);
    }

    [Fact]
    public void Duplicate_expands_paired_actions_and_shifts_only_when_required_for_order()
    {
        var service = CreateService(KeyDown(0), Move(5), KeyUp(10), Wheel(15));

        Assert.True(service.TryDuplicate([0]));
        var actions = service.Actions;
        Assert.True(actions.Select(action => action.Type).SequenceEqual([MacroActionType.KeyDown, MacroActionType.MouseMove,
            MacroActionType.KeyUp, MacroActionType.KeyDown, MacroActionType.KeyUp, MacroActionType.MouseWheel]));
        Assert.True(actions.Select(action => action.AtMs).SequenceEqual([0L, 5L, 10L, 11L, 21L, 22L]));
        AssertMonotonic(actions);
        AssertNoHeldInputs(actions);
    }

    [Fact]
    public void Deleting_either_half_removes_its_input_pair_and_selects_the_next_action()
    {
        var service = CreateService(KeyDown(0), Move(5), KeyUp(10), Wheel(15));

        Assert.True(service.TryDelete([0]));
        Assert.True(service.Actions.Select(action => action.Type).SequenceEqual([MacroActionType.MouseMove, MacroActionType.MouseWheel]));
        Assert.True(service.Actions.Select(action => action.AtMs).SequenceEqual([5L, 15L]));
        Assert.True(service.LastSelectionIndices.SequenceEqual([0]));
        AssertNoHeldInputs(service.Actions);
    }

    [Fact]
    public void Pause_and_delay_edits_adjust_absolute_times_without_breaking_order()
    {
        var service = CreateService(Move(0), KeyDown(10), KeyUp(20), Wheel(30));

        Assert.True(service.TryInsertPauseAfter(0, 500));
        Assert.True(service.Actions.Select(action => action.AtMs).SequenceEqual([0L, 510L, 520L, 530L]));
        Assert.True(service.TryEditAction(1, new MacroActionEdit(DelayFromPreviousMs: 25)));
        Assert.True(service.Actions.Select(action => action.AtMs).SequenceEqual([0L, 25L, 35L, 45L]));
        AssertMonotonic(service.Actions);
        AssertNoHeldInputs(service.Actions);
    }

    [Fact]
    public void Undo_redo_roundtrip_tracks_the_saved_state()
    {
        var service = CreateService(Move(0), Wheel(20));
        Assert.True(service.IsAtSavedState);

        Assert.True(service.TryInsertPauseAfter(0, 100));
        Assert.False(service.IsAtSavedState);
        Assert.True(service.TryEditAction(1, new MacroActionEdit(X: 42)));
        Assert.True(service.Actions[1].X == 42);

        service.Undo();
        Assert.True(service.Actions[1].X == 0);
        Assert.False(service.IsAtSavedState);
        service.Undo();
        Assert.True(service.Actions.Select(action => action.AtMs).SequenceEqual([0L, 20L]));
        Assert.True(service.IsAtSavedState);
        Assert.True(service.CanRedo);
        service.Redo();
        service.Redo();
        Assert.True(service.Actions[1].X == 42);
        Assert.False(service.IsAtSavedState);
        service.MarkSaved();
        Assert.True(service.IsAtSavedState);
    }

    [Fact]
    public void Undo_history_is_limited_to_one_hundred_operations_and_reset_clears_it()
    {
        var document = Document(Move(0));
        var service = new MacroEditService();
        service.Reset(document, document.Actions, isSaved: true);
        for (var value = 2; value <= 102; value++)
            Assert.True(service.TryEditAction(0, new MacroActionEdit(X: value)));

        for (var undo = 0; undo < MacroEditService.MaximumUndoDepth; undo++) service.Undo();
        Assert.True(service.Actions[0].X == 2);
        Assert.False(service.CanUndo);

        service.Reset(document, document.Actions, isSaved: true);
        Assert.False(service.CanUndo);
        Assert.False(service.CanRedo);
        Assert.True(service.IsAtSavedState);
    }

    [Fact]
    public void Invalid_edits_are_rejected_without_changing_actions_or_history()
    {
        var service = CreateService(ButtonDown(0), ButtonUp(20), Move(30));
        var before = service.Actions.Select(Signature).ToArray();

        Assert.False(service.TryEditAction(1, new MacroActionEdit(DelayFromPreviousMs: -1)));
        Assert.False(service.TryEditAction(0, new MacroActionEdit(Button: "unknown")));
        Assert.False(service.TryEditAction(1, new MacroActionEdit(DelayFromPreviousMs: MacroEditService.MaximumEditableGapMs + 1)));
        Assert.False(service.TryInsertPauseAfter(0, MacroEditService.MaximumEditableGapMs + 1));
        Assert.True(before.SequenceEqual(service.Actions.Select(Signature)));
        Assert.False(service.CanUndo);
        Assert.True(service.IsAtSavedState);

        var keyService = CreateService(KeyDown(0), KeyUp(1));
        Assert.False(keyService.TryEditAction(0, new MacroActionEdit(KeyCode: 256)));
        Assert.False(keyService.CanUndo);
    }

    [Fact]
    public void Key_and_mouse_button_value_edits_apply_to_their_pairs()
    {
        var service = CreateService(KeyDown(0), ButtonDown(10), ButtonUp(20), KeyUp(30));

        Assert.True(service.TryEditAction(0, new MacroActionEdit(KeyCode: 66)));
        Assert.True(service.Actions[0].KeyCode == 66);
        Assert.True(service.Actions[3].KeyCode == 66);
        Assert.True(service.TryEditAction(1, new MacroActionEdit(Button: MacroMouseButtons.X1)));
        Assert.True(service.Actions[1].Button == MacroMouseButtons.X1);
        Assert.True(service.Actions[2].Button == MacroMouseButtons.X1);
        AssertNoHeldInputs(service.Actions);
    }

    [Fact]
    public void Key_edit_rebuilds_virtual_key_metadata_for_extended_keys()
    {
        var service = CreateService(KeyDown(0), KeyUp(1));

        Assert.True(service.TryEditAction(0, new MacroActionEdit(KeyCode: 0x25)));
        Assert.True(service.Actions[0].ScanCode == 0 && service.Actions[0].RawFlags == 0x01);
        Assert.True(service.Actions[1].ScanCode == 0 && service.Actions[1].RawFlags == 0x81);
        AssertNoHeldInputs(service.Actions);
    }

    [Fact]
    public void Incomplete_input_pairs_cannot_be_duplicated_or_retargeted_as_single_actions()
    {
        var service = CreateService(KeyDown(0), ButtonDown(10), Move(20));
        var before = service.Actions.Select(Signature).ToArray();

        Assert.False(service.TryDuplicate([0]));
        Assert.False(service.TryDuplicate([1]));
        Assert.False(service.TryEditAction(0, new MacroActionEdit(KeyCode: 66)));
        Assert.False(service.TryEditAction(1, new MacroActionEdit(Button: MacroMouseButtons.X1)));
        Assert.True(before.SequenceEqual(service.Actions.Select(Signature)));
        Assert.False(service.CanUndo);
    }

    [Fact]
    public void Empty_single_action_and_boundary_moves_are_handled()
    {
        var empty = CreateService();
        Assert.False(empty.TryMove([0], moveUp: true));
        Assert.False(empty.TryDuplicate([0]));
        Assert.False(empty.TryDelete([0]));

        var single = CreateService(Move(5));
        Assert.False(single.TryMove([0], moveUp: true));
        Assert.False(single.TryMove([0], moveUp: false));
        Assert.True(single.TryDelete([0]));
        Assert.Empty(single.Actions);
        Assert.Empty(single.LastSelectionIndices);
    }

    private static MacroEditService CreateService(params MacroAction[] actions)
    {
        var document = Document(actions);
        var service = new MacroEditService();
        service.Reset(document, document.Actions, isSaved: true);
        return service;
    }

    private static MacroDocument Document(params MacroAction[] actions) => new()
    {
        Name = "Synthetic edit fixture",
        Actions = actions.ToList()
    };

    private static MacroAction Move(long atMs) => new() { AtMs = atMs, Type = MacroActionType.MouseMove, X = 1, Y = 2 };
    private static MacroAction Wheel(long atMs) => new() { AtMs = atMs, Type = MacroActionType.MouseWheel, WheelDelta = 120 };
    private static MacroAction KeyDown(long atMs) => new() { AtMs = atMs, Type = MacroActionType.KeyDown, KeyCode = 65, ScanCode = 30 };
    private static MacroAction KeyUp(long atMs) => new() { AtMs = atMs, Type = MacroActionType.KeyUp, KeyCode = 65, ScanCode = 30, RawFlags = 0x80 };
    private static MacroAction ButtonDown(long atMs) => new() { AtMs = atMs, Type = MacroActionType.MouseButtonDown, Button = MacroMouseButtons.Left };
    private static MacroAction ButtonUp(long atMs) => new() { AtMs = atMs, Type = MacroActionType.MouseButtonUp, Button = MacroMouseButtons.Left };
    private static string Signature(MacroAction action) => $"{action.AtMs}:{action.Type}:{action.KeyCode}:{action.Button}:{action.X}:{action.Y}:{action.WheelDelta}";

    private static void AssertMonotonic(IReadOnlyList<MacroAction> actions)
    {
        for (var index = 1; index < actions.Count; index++) Assert.True(actions[index].AtMs >= actions[index - 1].AtMs);
    }

    private static void AssertNoHeldInputs(IEnumerable<MacroAction> actions)
    {
        var keys = new HashSet<(int KeyCode, bool Extended)>();
        var buttons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case MacroActionType.KeyDown: keys.Add((action.KeyCode, (action.RawFlags & 1) != 0)); break;
                case MacroActionType.KeyUp: keys.Remove((action.KeyCode, (action.RawFlags & 1) != 0)); break;
                case MacroActionType.MouseButtonDown: buttons.Add(action.Button); break;
                case MacroActionType.MouseButtonUp: buttons.Remove(action.Button); break;
            }
        }
        Assert.True(keys.Count == 0);
        Assert.True(buttons.Count == 0);
    }
}
