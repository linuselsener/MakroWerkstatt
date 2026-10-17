using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

public sealed class MacroEditServiceTests
{
    private readonly ITestOutputHelper _output;

    public MacroEditServiceTests(ITestOutputHelper output) => _output = output;

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
    public void Clear_history_removes_and_scrubs_all_snapshots()
    {
        var service = CreateService(KeyDown(0), KeyUp(1), Move(2));
        Assert.True(service.TryEditAction(2, new MacroActionEdit(X: 42)));
        Assert.True(service.CanUndo);

        service.ClearHistory();

        Assert.Equal(0, service.HistoryCount);
        Assert.False(service.CanUndo);
        Assert.False(service.CanRedo);
        Assert.Empty(service.Actions);
        Assert.Empty(service.LastSelectionIndices);
    }

    [Fact]
    public void Seeded_random_edit_sequences_preserve_invariants_and_undo_history()
    {
        for (var seedOffset = 0; seedOffset < 50; seedOffset++)
        {
            var seed = 20_261_017 + seedOffset;
            var random = new Random(seed);
            var initialActions = CreateBalancedFixture();
            var initialSignature = FullSignature(initialActions);
            var service = CreateService(initialActions.ToArray());
            var successfulEdits = 0;

            for (var operationIndex = 0; operationIndex < 200; operationIndex++)
            {
                var before = CaptureServiceState(service);
                bool accepted;
                bool createsSnapshot;
                var operationKind = -1;
                try
                {
                    (accepted, createsSnapshot) = ApplyRandomOperation(service, random, out operationKind);
                }
                catch (Exception exception)
                {
                    throw RandomFailure(seed, operationIndex,
                        $"operation {operationKind} raised {exception.GetType().Name} ({exception.StackTrace})");
                }

                var after = CaptureServiceState(service);
                if (!accepted)
                    Ensure(before == after, seed, operationIndex, "rejected operation changed service state");
                if (accepted && createsSnapshot) successfulEdits++;

                try
                {
                    AssertRandomInvariants(service, seed, operationIndex);
                }
                catch (Xunit.Sdk.XunitException exception)
                {
                    throw RandomFailure(seed, operationIndex,
                        $"operation {operationKind} accepted={accepted}, actionCount={service.Actions.Count}: {exception.Message}");
                }
                if (service.CanUndo)
                {
                    var stateBeforeRoundTrip = FullSignature(service.Actions);
                    service.Undo();
                    AssertRandomInvariants(service, seed, operationIndex);
                    Ensure(service.CanRedo, seed, operationIndex, "undo did not enable redo");
                    service.Redo();
                    Ensure(stateBeforeRoundTrip == FullSignature(service.Actions), seed, operationIndex,
                        "undo followed by redo changed the state");
                }
            }

            if (successfulEdits <= MacroEditService.MaximumUndoDepth)
            {
                var operationIndex = 200;
                while (service.CanUndo)
                {
                    service.Undo();
                    AssertRandomInvariants(service, seed, operationIndex);
                }
                Ensure(initialSignature == FullSignature(service.Actions), seed, operationIndex,
                    "undoing all edits did not restore the initial state within the history limit");
            }
        }
    }

    [Fact]
    public void Large_action_list_snapshot_history_reports_runtime_and_memory()
    {
        var actions = Enumerable.Range(0, 5_000)
            .Select(index => new MacroAction { AtMs = index, Type = MacroActionType.MouseMove })
            .ToList();
        var document = Document(actions.ToArray());
        var service = new MacroEditService();
        service.Reset(document, document.Actions, isSaved: true);
        actions.Clear();
        document.Actions.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var maximumOperationMs = 0d;

        for (var edit = 1; edit <= MacroEditService.MaximumUndoDepth; edit++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var accepted = service.TryEditAction(2_500, new MacroActionEdit(X: edit));
            timer.Stop();
            maximumOperationMs = Math.Max(maximumOperationMs, timer.Elapsed.TotalMilliseconds);
            Ensure(accepted, 0, edit, "large-list edit was rejected");
        }

        var retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - baselineBytes;
        _output.WriteLine(
            $"Large-list snapshots: 5,000 actions, 100 edits, max operation {maximumOperationMs:F1} ms, retained heap delta {retainedBytes:N0} bytes.");
        Ensure(service.HistoryCount <= MacroEditService.MaximumUndoDepth + 1, 0,
            MacroEditService.MaximumUndoDepth, "large-list history exceeded its configured depth");
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
    public void Duplicate_and_delete_reject_selections_without_valid_indices()
    {
        var service = CreateService(Move(0), Wheel(10));
        var before = FullSignature(service.Actions);
        var historyCount = service.HistoryCount;

        Assert.False(service.TryDuplicate([-1, 2]));
        Assert.False(service.TryDelete([-1, 2]));

        Assert.True(before == FullSignature(service.Actions));
        Assert.Equal(historyCount, service.HistoryCount);
        Assert.False(service.CanUndo);
        Assert.False(service.CanRedo);
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
    public void Repeated_virtual_key_values_keep_each_transition_pair_after_edit()
    {
        var service = CreateService(
            KeyDown(0),
            new MacroAction { AtMs = 1, Type = MacroActionType.KeyDown, KeyCode = 66, ScanCode = 48 },
            new MacroAction { AtMs = 2, Type = MacroActionType.KeyUp, KeyCode = 65, ScanCode = 30, RawFlags = 0x80 },
            new MacroAction { AtMs = 3, Type = MacroActionType.KeyUp, KeyCode = 66, ScanCode = 48, RawFlags = 0x80 });

        Assert.True(service.TryEditAction(0, new MacroActionEdit(KeyCode: 66)));
        Assert.True(service.TryDelete([1]));

        Assert.True(service.Actions.Select(action => action.Type)
            .SequenceEqual([MacroActionType.KeyDown, MacroActionType.KeyUp]));
        Assert.True(service.Actions[0].KeyCode == service.Actions[1].KeyCode);
        Assert.Equal(1, service.Actions.Count(action => action.Type == MacroActionType.KeyDown));
        Assert.Equal(1, service.Actions.Count(action => action.Type == MacroActionType.KeyUp));
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

    private static List<MacroAction> CreateBalancedFixture() =>
    [
        KeyDown(0), Move(1), KeyUp(2), ButtonDown(3), Wheel(4), ButtonUp(5),
        new MacroAction { AtMs = 6, Type = MacroActionType.KeyDown, KeyCode = 66, ScanCode = 48 },
        new MacroAction { AtMs = 7, Type = MacroActionType.KeyUp, KeyCode = 66, ScanCode = 48, RawFlags = 0x80 },
        new MacroAction { AtMs = 8, Type = MacroActionType.MouseButtonDown, Button = MacroMouseButtons.Right },
        new MacroAction { AtMs = 9, Type = MacroActionType.MouseButtonUp, Button = MacroMouseButtons.Right },
        new MacroAction { AtMs = 10, Type = MacroActionType.MouseMoveRelative, X = -2, Y = 3 },
        Wheel(11), Move(12)
    ];

    private static (bool Accepted, bool CreatesSnapshot) ApplyRandomOperation(MacroEditService service, Random random,
        out int operationKind)
    {
        var actionCount = service.Actions.Count;
        var firstIndex = random.Next(-1, actionCount + 1);
        var secondIndex = random.Next(-1, actionCount + 1);
        var selection = firstIndex == secondIndex ? [firstIndex] : new[] { firstIndex, secondIndex };
        operationKind = random.Next(8);
        switch (operationKind)
        {
            case 0:
                return (service.TryMove(selection, moveUp: random.Next(2) == 0), true);
            case 1:
                return (service.TryDuplicate(selection), true);
            case 2:
                return (service.TryDelete(selection), true);
            case 3:
                return (service.TryInsertPauseAfter(random.Next(-1, actionCount + 1), random.Next(1, 1_001)), true);
            case 4:
                var delay = random.Next(6) == 0
                    ? MacroEditService.MaximumEditableGapMs + 1
                    : random.Next(0, 1_001);
                return (service.TryEditAction(random.Next(-1, actionCount + 1),
                    new MacroActionEdit(DelayFromPreviousMs: delay)), true);
            case 5:
                return (TryRandomValueEdit(service, random), true);
            case 6:
                var canUndo = service.CanUndo;
                service.Undo();
                return (canUndo, false);
            default:
                var canRedo = service.CanRedo;
                service.Redo();
                return (canRedo, false);
        }
    }

    private static bool TryRandomValueEdit(MacroEditService service, Random random)
    {
        var actions = service.Actions;
        var index = random.Next(-1, actions.Count + 1);
        if (index < 0 || index >= actions.Count)
            return service.TryEditAction(index, new MacroActionEdit(X: random.Next(-100, 101)));

        var action = actions[index];
        var edit = action.Type switch
        {
            MacroActionType.KeyDown or MacroActionType.KeyUp => new MacroActionEdit(KeyCode: random.Next(256)),
            MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp => new MacroActionEdit(
                Button: MacroMouseButtons.AllowedValues[random.Next(MacroMouseButtons.AllowedValues.Count)]),
            MacroActionType.MouseWheel => new MacroActionEdit(WheelDelta: random.Next(-2_000, 2_001)),
            _ => new MacroActionEdit(X: random.Next(-100, 101), Y: random.Next(-100, 101))
        };
        if (random.Next(20) == 0) edit = edit with { KeyCode = 256 };
        return service.TryEditAction(index, edit);
    }

    private static string CaptureServiceState(MacroEditService service) => string.Join("\n",
        FullSignature(service.Actions),
        service.CanUndo,
        service.CanRedo,
        service.IsAtSavedState,
        service.HistoryCount,
        string.Join(",", service.LastSelectionIndices));

    private static void AssertRandomInvariants(MacroEditService service, int seed, int operationIndex)
    {
        var actions = service.Actions;
        try
        {
            MacroStorage.ValidateDocument(new MacroDocument
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Seed-based edit fixture",
                FormatVersion = MacroStorage.PayloadFormatVersion,
                Actions = actions.ToList()
            });
        }
        catch (Exception exception)
        {
            throw RandomFailure(seed, operationIndex, $"storage validation failed ({exception.GetType().Name})");
        }

        for (var index = 0; index < actions.Count; index++)
            Ensure(actions[index].AtMs >= 0 && (index == 0 || actions[index].AtMs >= actions[index - 1].AtMs),
                seed, operationIndex, "timestamps are invalid or non-monotonic");

        var keyBalance = new Dictionary<(int KeyCode, bool Extended), int>();
        var buttonBalance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case MacroActionType.KeyDown:
                    AddBalance(keyBalance, (action.KeyCode, (action.RawFlags & 1) != 0), 1);
                    break;
                case MacroActionType.KeyUp:
                    var key = (action.KeyCode, (action.RawFlags & 1) != 0);
                    AddBalance(keyBalance, key, -1);
                    Ensure(keyBalance[key] >= 0, seed, operationIndex,
                        "a key release precedes its matching press");
                    break;
                case MacroActionType.MouseButtonDown:
                    AddBalance(buttonBalance, action.Button, 1);
                    break;
                case MacroActionType.MouseButtonUp:
                    AddBalance(buttonBalance, action.Button, -1);
                    Ensure(buttonBalance[action.Button] >= 0, seed, operationIndex,
                        "a mouse release precedes its matching press");
                    break;
            }
        }
        Ensure(keyBalance.Values.All(balance => balance == 0) && buttonBalance.Values.All(balance => balance == 0),
            seed, operationIndex, "an input remains held at the sequence end");
    }

    private static void AddBalance<TKey>(Dictionary<TKey, int> balances, TKey key, int change) where TKey : notnull
    {
        balances.TryGetValue(key, out var current);
        balances[key] = current + change;
    }

    private static string FullSignature(IEnumerable<MacroAction> actions) => string.Join("|", actions.Select(action =>
        $"{action.AtMs}:{(int)action.Type}:{action.X}:{action.Y}:{action.KeyCode}:{action.ScanCode}:{action.RawFlags}:{action.WheelDelta}:{action.Button}"));

    private static void Ensure(bool condition, int seed, int operationIndex, string invariant)
    {
        if (!condition) throw RandomFailure(seed, operationIndex, invariant);
    }

    private static Xunit.Sdk.XunitException RandomFailure(int seed, int operationIndex, string invariant) =>
        new($"Seed {seed}, operation {operationIndex}: {invariant}.");

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
