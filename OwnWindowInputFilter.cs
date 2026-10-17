namespace MakroWerkstatt;

public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom, bool IsMinimized = false)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public interface IWindowBoundsProvider
{
    bool HasWindow { get; }
    bool IsForeground { get; }
    bool TryGetBounds(out WindowBounds bounds);
}

/// <summary>Determines whether a captured action originated within this application's window.</summary>
public sealed class OwnWindowInputFilter
{
    private readonly IWindowBoundsProvider _boundsProvider;

    public OwnWindowInputFilter(IWindowBoundsProvider boundsProvider) =>
        _boundsProvider = boundsProvider ?? throw new ArgumentNullException(nameof(boundsProvider));

    public bool ShouldIgnore(MacroAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_boundsProvider.HasWindow) return false;
        if (action.Type is MacroActionType.KeyDown or MacroActionType.KeyUp or MacroActionType.MouseMoveRelative)
            return _boundsProvider.IsForeground;

        return _boundsProvider.TryGetBounds(out var bounds) && bounds.Contains(action.X, action.Y);
    }
}
