using System.Runtime.InteropServices;

namespace TwinDock.Native;

internal sealed class OutsideClickWatcher : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;

    private readonly HookProc _proc;
    private nint _hook;

    public event Action<int, int>? ButtonDown;

    public OutsideClickWatcher()
    {
        _proc = HookCallback;
    }

    public bool IsActive => _hook != 0;

    public void Start()
    {
        if (_hook != 0)
        {
            return;
        }

        try
        {
            _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        }
        catch
        {
            _hook = 0;
        }
    }

    public void Stop()
    {
        if (_hook == 0)
        {
            return;
        }

        var hook = _hook;
        _hook = 0;
        try
        {
            UnhookWindowsHookEx(hook);
        }
        catch
        {
            // Unhook is best-effort during shutdown.
        }
    }

    public void Dispose() => Stop();

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && lParam != 0 && (wParam == (nint)WmLButtonDown || wParam == (nint)WmRButtonDown))
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                ButtonDown?.Invoke(info.Point.X, info.Point.Y);
            }
        }
        catch
        {
            // Never throw out of a low-level hook.
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointL Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
