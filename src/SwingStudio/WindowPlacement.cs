using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SwingStudio;

/// <summary>Win32 window placement. Rectangles are in workspace pixels, the same space GetWindowPlacement reports.</summary>
internal static class WindowPlacement
{
    private const int SwHide = 0;
    private const int SwShowNormal = 1;
    private const int SwShowMaximized = 3;
    private const uint MonitorDefaultToNull = 0;

    public static Int32Rect? NormalBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = NewPlacement();
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return null;
        }

        var rect = placement.NormalPosition;
        return new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static bool IsOnAMonitor(Int32Rect bounds)
    {
        var rect = new Rect32 { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
        return MonitorFromRect(ref rect, MonitorDefaultToNull) != IntPtr.Zero;
    }

    /// <summary>Moves the window's normal bounds. A window that is not shown yet stays hidden and keeps its WPF WindowState.</summary>
    public static void Apply(Window window, Int32Rect bounds, bool maximized)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = NewPlacement();
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return;
        }

        placement.NormalPosition = new Rect32 { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
        placement.ShowCmd = !window.IsVisible ? SwHide : maximized ? SwShowMaximized : SwShowNormal;
        placement.Flags = 0;
        SetWindowPlacement(handle, ref placement);
    }

    private static WindowPlacementData NewPlacement() => new() { Length = Marshal.SizeOf<WindowPlacementData>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacementData
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point32 MinPosition;
        public Point32 MaxPosition;
        public Rect32 NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacementData placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacementData placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);
}
