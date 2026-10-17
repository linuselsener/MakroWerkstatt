using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MakroWerkstatt;

/// <summary>Reads the real bounds and foreground state of a WPF window.</summary>
public sealed class WpfWindowBoundsProvider(Window window) : IWindowBoundsProvider
{
    public bool HasWindow => GetHandle() != IntPtr.Zero;

    public bool IsForeground
    {
        get
        {
            var handle = GetHandle();
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }
    }

    public bool TryGetBounds(out WindowBounds bounds)
    {
        var handle = GetHandle();
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            bounds = default;
            return false;
        }

        bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom,
            window.WindowState == WindowState.Minimized);
        return true;
    }

    private IntPtr GetHandle() => new WindowInteropHelper(window).Handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
