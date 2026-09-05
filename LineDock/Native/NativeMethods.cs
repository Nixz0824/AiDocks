using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LineDock.Native;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExNoActivate = 0x08000000L;
    public const int WmNcHitTest = 0x0084;
    public const int WmDisplayChange = 0x007E;
    public const int HtTransparent = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PointL
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint newLong);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out PointL point);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    public static void ConfigureUtilityWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongPtr64(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr64(handle, GwlExStyle, new nint(style | WsExToolWindow | WsExNoActivate));
        var corner = DwmwcpDoNotRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var border = DwmwaColorNone;
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
    }

    public static int SignedLowWord(nint value) => unchecked((short)(long)value);
    public static int SignedHighWord(nint value) => unchecked((short)((long)value >> 16));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(nint window, nint rect, bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    public static void RefreshLayeredWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        InvalidateRect(handle, 0, false);
        UpdateWindow(handle);
        DwmFlush();
    }
}
