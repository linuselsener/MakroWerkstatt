using System.IO;

namespace MakroWerkstatt;

/// <summary>Edits macro actions without depending on WPF controls.</summary>
public sealed class MacroEditService
{
    public const long MaximumEditableGapMs = 24L * 60 * 60 * 1000;
    public const int MaximumUndoDepth = 100;

    private readonly List<Snapshot> _history = [];
    private int _historyIndex;
    private long _nextStateId;
    private long? _savedStateId;
    private MacroDocument _document = new();

    public bool CanUndo => _historyIndex > 0;
    public bool CanRedo => _historyIndex < _history.Count - 1;
    public bool IsAtSavedState => _historyIndex >= 0 && _savedStateId == Current.StateId;
    public IReadOnlyList<MacroAction> Actions => _historyIndex < 0 ? [] : Current.Actions.Select(Clone).ToArray();
    public IReadOnlyList<int> LastSelectionIndices { get; private set; } = [];
    internal int HistoryCount => _history.Count;

    public MacroEditService() => Reset(new MacroDocument(), [], isSaved: true);

    public void Reset(MacroDocument document, IEnumerable<MacroAction> actions, bool isSaved)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(actions);
        ClearHistory();
        _document = CloneDocument(document);
        var snapshot = new Snapshot(++_nextStateId, actions.Select(Clone).ToList());
        _history.Add(snapshot);
        _historyIndex = 0;
        _savedStateId = isSaved ? snapshot.StateId : null;
        LastSelectionIndices = [];
    }

    public void ClearHistory()
    {
        foreach (var snapshot in _history)
        {
            ClearActions(snapshot.Actions);
        }
        _history.Clear();
        ClearActions(_document.Actions);
        _document = new MacroDocument { Actions = [] };
        _historyIndex = -1;
        _savedStateId = null;
        LastSelectionIndices = [];
    }

    public void UpdateDocument(MacroDocument document) => _document = CloneDocument(document);

    public void MarkSaved() => _savedStateId = Current.StateId;

    public bool CanMove(IEnumerable<int> selectedIndices, bool moveUp)
    {
        var actions = Current.Actions;
        var requestedIndices = selectedIndices.Where(index => index >= 0 && index < actions.Count).Distinct().ToArray();
        if (requestedIndices.Length == 0) return false;
        var selected = ExpandToPairedActions(actions, requestedIndices);
        if (moveUp)
        {
            for (var index = 1; index < selected.Length; index++)
                if (selected[index] && !selected[index - 1]) return true;
        }
        else
        {
            for (var index = selected.Length - 2; index >= 0; index--)
                if (selected[index] && !selected[index + 1]) return true;
        }
        return false;
    }

    public bool TryMove(IEnumerable<int> selectedIndices, bool moveUp)
    {
        var original = Current.Actions;
        var selected = ExpandToPairedActions(original, selectedIndices);
        if (selected.Length == 0) return false;

        var proposed = original.Select(Clone).ToList();
        var selectedSlots = selected.ToArray();
        var changed = false;
        if (moveUp)
        {
            for (var index = 1; index < proposed.Count; index++)
            {
                if (!selectedSlots[index] || selectedSlots[index - 1]) continue;
                (proposed[index - 1], proposed[index]) = (proposed[index], proposed[index - 1]);
                (selectedSlots[index - 1], selectedSlots[index]) = (selectedSlots[index], selectedSlots[index - 1]);
                changed = true;
            }
        }
        else
        {
            for (var index = proposed.Count - 2; index >= 0; index--)
            {
                if (!selectedSlots[index] || selectedSlots[index + 1]) continue;
                (proposed[index + 1], proposed[index]) = (proposed[index], proposed[index + 1]);
                (selectedSlots[index + 1], selectedSlots[index]) = (selectedSlots[index], selectedSlots[index + 1]);
                changed = true;
            }
        }
        if (!changed) return false;

        for (var index = 0; index < proposed.Count; index++)
            proposed[index].AtMs = original[index].AtMs;
        return TryCommit(proposed, selectedSlots.Select((value, index) => (value, index))
            .Where(item => item.value).Select(item => item.index).ToHashSet());
    }

    public bool TryDuplicate(IEnumerable<int> selectedIndices)
    {
        var original = Current.Actions;
        var selected = ExpandToPairedActions(original, selectedIndices);
        if (selected.Length == 0 || original.Count + selected.Length > MacroStorage.MaximumActionCount) return false;

        var sourceIndices = Enumerable.Range(0, original.Count).Where(index => selected[index]).ToArray();
        var pairs = FindPairs(original);
        if (sourceIndices.Any(index => IsInputTransition(original[index].Type) && !pairs.ContainsKey(index))) return false;
        var firstSource = original[sourceIndices[0]].AtMs;
        var lastSourceIndex = sourceIndices[^1];
        var insertIndex = lastSourceIndex + 1;
        var previousTime = original[lastSourceIndex].AtMs;
        if (previousTime == long.MaxValue) return false;
        var baseTime = previousTime + 1;
        var copies = new List<MacroAction>(sourceIndices.Length);
        try
        {
            foreach (var sourceIndex in sourceIndices)
            {
                var copy = Clone(original[sourceIndex]);
                copy.AtMs = checked(baseTime + (original[sourceIndex].AtMs - firstSource));
                copies.Add(copy);
            }
            var followingTime = insertIndex < original.Count ? original[insertIndex].AtMs : long.MaxValue;
            var shiftSuffixBy = insertIndex < original.Count && copies[^1].AtMs >= followingTime
                ? checked(copies[^1].AtMs - followingTime + 1)
                : 0;

            var proposed = original.Select(Clone).ToList();
            for (var offset = 0; offset < copies.Count; offset++)
                proposed.Insert(insertIndex + offset, copies[offset]);
            if (shiftSuffixBy > 0)
            {
                for (var index = insertIndex + copies.Count; index < proposed.Count; index++)
                    proposed[index].AtMs = checked(proposed[index].AtMs + shiftSuffixBy);
            }

            var newSelection = Enumerable.Range(insertIndex, copies.Count).ToHashSet();
            return TryCommit(proposed, newSelection);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryDelete(IEnumerable<int> selectedIndices)
    {
        var original = Current.Actions;
        var selected = ExpandToPairedActions(original, selectedIndices);
        if (selected.Length == 0) return false;
        var proposed = original.Where((_, index) => !selected[index]).Select(Clone).ToList();
        var nextSelectionIndex = Math.Min(Array.FindIndex(selected, value => value), proposed.Count - 1);
        var selection = nextSelectionIndex >= 0 && proposed.Count > 0 ? new HashSet<int> { nextSelectionIndex } : [];
        return TryCommit(proposed, selection);
    }

    public bool TryInsertPauseAfter(int index, long durationMs)
    {
        var original = Current.Actions;
        if (index < 0 || index >= original.Count - 1 || durationMs is < 1 or > MaximumEditableGapMs)
            return false;
        var proposed = original.Select(Clone).ToList();
        try
        {
            for (var position = index + 1; position < proposed.Count; position++)
                proposed[position].AtMs = checked(proposed[position].AtMs + durationMs);
        }
        catch (OverflowException)
        {
            return false;
        }
        return TryCommit(proposed, new HashSet<int> { index });
    }

    public bool TryEditAction(int index, MacroActionEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var original = Current.Actions;
        if (index < 0 || index >= original.Count) return false;
        if (edit.DelayFromPreviousMs is < 0 or > MaximumEditableGapMs) return false;
        if (edit.KeyCode is < 0 or > 255) return false;
        if (edit.Button is not null && !MacroMouseButtons.IsAllowed(edit.Button)) return false;

        var proposed = original.Select(Clone).ToList();
        var selectedAction = proposed[index];
        var pairMap = FindPairs(proposed);
        var changed = false;
        if (edit.KeyCode.HasValue && selectedAction.Type is MacroActionType.KeyDown or MacroActionType.KeyUp)
        {
            if (!pairMap.TryGetValue(index, out var partner)) return false;
            var extended = IsExtendedVirtualKeyCode(edit.KeyCode.Value);
            changed = selectedAction.KeyCode != edit.KeyCode.Value || proposed[partner].KeyCode != edit.KeyCode.Value
                || selectedAction.ScanCode != 0 || proposed[partner].ScanCode != 0
                || selectedAction.RawFlags != ExpectedKeyFlags(selectedAction.Type, extended)
                || proposed[partner].RawFlags != ExpectedKeyFlags(proposed[partner].Type, extended);
            UpdateKey(selectedAction, edit.KeyCode.Value, extended);
            UpdateKey(proposed[partner], edit.KeyCode.Value, extended);
        }
        if (edit.Button is not null && selectedAction.Type is MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp)
        {
            if (!pairMap.TryGetValue(index, out var partner)) return false;
            changed |= selectedAction.Button != edit.Button || proposed[partner].Button != edit.Button;
            selectedAction.Button = edit.Button;
            proposed[partner].Button = edit.Button;
        }
        if (edit.X.HasValue)
        {
            changed |= selectedAction.X != edit.X.Value;
            selectedAction.X = edit.X.Value;
        }
        if (edit.Y.HasValue)
        {
            changed |= selectedAction.Y != edit.Y.Value;
            selectedAction.Y = edit.Y.Value;
        }
        if (edit.WheelDelta.HasValue && selectedAction.Type == MacroActionType.MouseWheel)
        {
            changed |= selectedAction.WheelDelta != edit.WheelDelta.Value;
            selectedAction.WheelDelta = edit.WheelDelta.Value;
        }

        if (edit.DelayFromPreviousMs.HasValue)
        {
            var previousTime = index == 0 ? 0 : proposed[index - 1].AtMs;
            long requestedTime;
            try { requestedTime = checked(previousTime + edit.DelayFromPreviousMs.Value); }
            catch (OverflowException) { return false; }
            var delta = requestedTime - proposed[index].AtMs;
            changed |= delta != 0;
            try
            {
                for (var position = index; position < proposed.Count; position++)
                    proposed[position].AtMs = checked(proposed[position].AtMs + delta);
            }
            catch (OverflowException) { return false; }
        }

        if (!changed) return false;
        return TryCommit(proposed, new HashSet<int> { index });
    }

    public IReadOnlyList<MacroAction> Undo()
    {
        if (CanUndo) _historyIndex--;
        return Actions;
    }

    public IReadOnlyList<MacroAction> Redo()
    {
        if (CanRedo) _historyIndex++;
        return Actions;
    }

    private Snapshot Current => _history[_historyIndex];

    private static void ClearActions(List<MacroAction> actions)
    {
        foreach (var action in actions)
        {
            action.AtMs = 0;
            action.Type = default;
            action.X = 0;
            action.Y = 0;
            action.KeyCode = 0;
            action.ScanCode = 0;
            action.RawFlags = 0;
            action.WheelDelta = 0;
            action.Button = string.Empty;
        }
        actions.Clear();
    }

    private bool TryCommit(List<MacroAction> proposed, HashSet<int> selectedIndices)
    {
        if (!HasSameFinalInputState(Current.Actions, proposed)) return false;
        try
        {
            var document = CloneDocument(_document);
            document.Actions = proposed;
            MacroStorage.ValidateDocument(document);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new Snapshot(++_nextStateId, proposed.Select(Clone).ToList()));
        _historyIndex = _history.Count - 1;
        if (_history.Count > MaximumUndoDepth + 1)
        {
            var removedStateId = _history[0].StateId;
            _history.RemoveAt(0);
            _historyIndex--;
            if (_savedStateId == removedStateId) _savedStateId = null;
        }
        LastSelectionIndices = selectedIndices.Where(index => index >= 0 && index < proposed.Count).Order().ToArray();
        return true;
    }

    private static bool[] ExpandToPairedActions(List<MacroAction> actions, IEnumerable<int> selectedIndices)
    {
        var selected = new bool[actions.Count];
        foreach (var index in selectedIndices.Distinct())
            if (index >= 0 && index < selected.Length) selected[index] = true;
        var pairs = FindPairs(actions);
        for (var index = 0; index < selected.Length; index++)
            if (selected[index] && pairs.TryGetValue(index, out var partner)) selected[partner] = true;
        return selected;
    }

    private static Dictionary<int, int> FindPairs(IReadOnlyList<MacroAction> actions)
    {
        var pairs = new Dictionary<int, int>();
        var keys = new Dictionary<(int KeyCode, bool Extended), int>();
        var buttons = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (action.Type == MacroActionType.KeyDown)
            {
                var key = (action.KeyCode, (action.RawFlags & 0x01) != 0);
                if (!keys.ContainsKey(key)) keys[key] = index;
            }
            else if (action.Type == MacroActionType.KeyUp)
            {
                var key = (action.KeyCode, (action.RawFlags & 0x01) != 0);
                if (keys.Remove(key, out var downIndex))
                {
                    pairs[index] = downIndex;
                    pairs[downIndex] = index;
                }
            }
            else if (action.Type == MacroActionType.MouseButtonDown)
            {
                if (!buttons.ContainsKey(action.Button)) buttons[action.Button] = index;
            }
            else if (action.Type == MacroActionType.MouseButtonUp && buttons.Remove(action.Button, out var downIndex))
            {
                pairs[index] = downIndex;
                pairs[downIndex] = index;
            }
        }
        return pairs;
    }

    private static bool IsInputTransition(MacroActionType type) => type is MacroActionType.KeyDown or MacroActionType.KeyUp
        or MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp;

    private static bool HasSameFinalInputState(IReadOnlyList<MacroAction> before, IReadOnlyList<MacroAction> after)
    {
        static (HashSet<(int, bool)> Keys, HashSet<string> Buttons) HeldInputs(IReadOnlyList<MacroAction> actions)
        {
            var keys = new HashSet<(int, bool)>();
            var buttons = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                switch (action.Type)
                {
                    case MacroActionType.KeyDown:
                        keys.Add((action.KeyCode, (action.RawFlags & 0x01) != 0));
                        break;
                    case MacroActionType.KeyUp:
                        keys.Remove((action.KeyCode, (action.RawFlags & 0x01) != 0));
                        break;
                    case MacroActionType.MouseButtonDown:
                        buttons.Add(action.Button);
                        break;
                    case MacroActionType.MouseButtonUp:
                        buttons.Remove(action.Button);
                        break;
                }
            }
            return (keys, buttons);
        }

        var previous = HeldInputs(before);
        var next = HeldInputs(after);
        return next.Keys.IsSubsetOf(previous.Keys) && next.Buttons.IsSubsetOf(previous.Buttons);
    }

    private static void UpdateKey(MacroAction action, int keyCode, bool extended)
    {
        action.KeyCode = keyCode;
        action.ScanCode = 0;
        action.RawFlags = ExpectedKeyFlags(action.Type, extended);
    }

    private static int ExpectedKeyFlags(MacroActionType type, bool extended) =>
        (type == MacroActionType.KeyUp ? 0x80 : 0) | (extended ? 0x01 : 0);

    private static bool IsExtendedVirtualKeyCode(int keyCode) => keyCode is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or
        0xA3 or 0xA5 or >= 0xA6 and <= 0xB7;

    private static MacroAction Clone(MacroAction action) => new()
    {
        AtMs = action.AtMs,
        Type = action.Type,
        X = action.X,
        Y = action.Y,
        KeyCode = action.KeyCode,
        ScanCode = action.ScanCode,
        RawFlags = action.RawFlags,
        WheelDelta = action.WheelDelta,
        Button = action.Button
    };

    private static MacroDocument CloneDocument(MacroDocument document) => new()
    {
        FormatVersion = document.FormatVersion,
        Application = document.Application,
        Id = document.Id,
        Name = document.Name,
        CreatedUtc = document.CreatedUtc,
        UpdatedUtc = document.UpdatedUtc,
        Actions = document.Actions?.Select(Clone).ToList() ?? []
    };

    private sealed record Snapshot(long StateId, List<MacroAction> Actions);
}

public sealed record MacroActionEdit(long? DelayFromPreviousMs = null, int? KeyCode = null, string? Button = null,
    int? X = null, int? Y = null, int? WheelDelta = null);
