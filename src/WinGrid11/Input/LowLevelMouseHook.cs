using System.Runtime.InteropServices;
using WinGrid11.Win32;

namespace WinGrid11.Input;

internal enum MouseButton { Left, Right }

internal sealed class MouseHookEventArgs : EventArgs
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public MouseButton? Button { get; init; }
    /// <summary>If set true by a subscriber, the event is swallowed and not delivered to other apps.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Process-local low-level mouse hook (WH_MOUSE_LL). No DLL injection;
/// the hook proc runs on the thread that installed it (must have a
/// message pump - WPF dispatcher satisfies this).
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private readonly Native.HookProc _proc;
    private IntPtr _hook;

    public event EventHandler<MouseHookEventArgs>? MouseMove;
    public event EventHandler<MouseHookEventArgs>? MouseDown;
    public event EventHandler<MouseHookEventArgs>? MouseUp;

    public LowLevelMouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWindowsHookEx(WH_MOUSE_LL) failed: " + Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != Native.HC_ACTION)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

        // Pass through events we ourselves injected (tagged via dwExtraInfo).
        // Critical: our SendInput LBUTTONUP that breaks the modal sizing
        // loop must NOT be processed as the user releasing LMB.
        if (data.dwExtraInfo == Native.WINGRID11_INJECTED_MAGIC)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        var args = new MouseHookEventArgs { X = data.pt.X, Y = data.pt.Y };

        try
        {
            switch (msg)
            {
                case Native.WM_MOUSEMOVE:
                    MouseMove?.Invoke(this, args);
                    break;
                case Native.WM_LBUTTONDOWN:
                    args = new MouseHookEventArgs { X = data.pt.X, Y = data.pt.Y, Button = MouseButton.Left };
                    MouseDown?.Invoke(this, args);
                    break;
                case Native.WM_LBUTTONUP:
                    args = new MouseHookEventArgs { X = data.pt.X, Y = data.pt.Y, Button = MouseButton.Left };
                    MouseUp?.Invoke(this, args);
                    break;
                case Native.WM_RBUTTONDOWN:
                    args = new MouseHookEventArgs { X = data.pt.X, Y = data.pt.Y, Button = MouseButton.Right };
                    MouseDown?.Invoke(this, args);
                    break;
                case Native.WM_RBUTTONUP:
                    args = new MouseHookEventArgs { X = data.pt.X, Y = data.pt.Y, Button = MouseButton.Right };
                    MouseUp?.Invoke(this, args);
                    break;
            }
        }
        catch
        {
            // Never let an exception escape the hook proc - Windows will silently uninstall it.
        }

        if (args.Handled)
            return new IntPtr(1);

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
