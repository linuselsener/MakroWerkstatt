using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

public sealed class OwnWindowInputFilterTests
{
    [Fact]
    public void Absolute_pointer_actions_use_half_open_window_bounds()
    {
        var filter = new OwnWindowInputFilter(new FakeBoundsProvider
        {
            Bounds = new WindowBounds(100, 50, 300, 200)
        });

        Assert.True(filter.ShouldIgnore(Pointer(100, 50)));
        Assert.True(filter.ShouldIgnore(Pointer(299, 199)));
        Assert.False(filter.ShouldIgnore(Pointer(300, 100)));
        Assert.False(filter.ShouldIgnore(Pointer(100, 200)));
        Assert.False(filter.ShouldIgnore(Pointer(99, 50)));
    }

    [Fact]
    public void Missing_handle_or_unavailable_bounds_do_not_filter_actions()
    {
        var missingHandle = new OwnWindowInputFilter(new FakeBoundsProvider { HasWindow = false });
        var unavailableBounds = new OwnWindowInputFilter(new FakeBoundsProvider { BoundsAvailable = false });

        Assert.False(missingHandle.ShouldIgnore(Pointer(10, 10)));
        Assert.False(unavailableBounds.ShouldIgnore(Pointer(10, 10)));
    }

    [Fact]
    public void Keyboard_and_relative_motion_actions_use_foreground_state()
    {
        var foreground = new OwnWindowInputFilter(new FakeBoundsProvider { IsForeground = true });
        var background = new OwnWindowInputFilter(new FakeBoundsProvider { IsForeground = false });

        Assert.True(foreground.ShouldIgnore(new MacroAction { Type = MacroActionType.KeyDown }));
        Assert.True(foreground.ShouldIgnore(new MacroAction { Type = MacroActionType.MouseMoveRelative }));
        Assert.False(background.ShouldIgnore(new MacroAction { Type = MacroActionType.KeyUp }));
        Assert.False(background.ShouldIgnore(new MacroAction { Type = MacroActionType.MouseMoveRelative }));
    }

    [Fact]
    public void Minimized_window_and_negative_monitor_coordinates_keep_native_bounds_semantics()
    {
        var minimized = new OwnWindowInputFilter(new FakeBoundsProvider
        {
            IsForeground = false,
            Bounds = new WindowBounds(-1920, -200, 0, 880, IsMinimized: true)
        });

        Assert.True(minimized.ShouldIgnore(Pointer(-1920, -200)));
        Assert.True(minimized.ShouldIgnore(Pointer(-1, 879)));
        Assert.False(minimized.ShouldIgnore(Pointer(0, 0)));
        Assert.False(minimized.ShouldIgnore(new MacroAction { Type = MacroActionType.KeyDown }));
    }

    [Fact]
    public void Coordinates_are_compared_without_dpi_or_monitor_offset_conversion()
    {
        var filter = new OwnWindowInputFilter(new FakeBoundsProvider
        {
            Bounds = new WindowBounds(150, 75, 450, 300)
        });

        Assert.True(filter.ShouldIgnore(Pointer(151, 76)));
        Assert.False(filter.ShouldIgnore(Pointer(100, 50)));
    }

    private static MacroAction Pointer(int x, int y) => new()
    {
        Type = MacroActionType.MouseMove,
        X = x,
        Y = y
    };

    private sealed class FakeBoundsProvider : IWindowBoundsProvider
    {
        public bool HasWindow { get; init; } = true;
        public bool IsForeground { get; init; }
        public bool BoundsAvailable { get; init; } = true;
        public WindowBounds Bounds { get; init; }

        public bool TryGetBounds(out WindowBounds bounds)
        {
            bounds = Bounds;
            return BoundsAvailable;
        }
    }
}
