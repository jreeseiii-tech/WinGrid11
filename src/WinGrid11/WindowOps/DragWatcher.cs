using WinGrid11.Win32;

namespace WinGrid11.WindowOps;

/// <summary>
/// Detects when any top-level window enters or leaves a system move/size
/// modal loop, entirely out-of-process via SetWinEventHook. No DLL injection.
/// HWNDs are resolved to GA_ROOT so Groupy 2's tab host (the actually
/// movable window) is reported, not the inner tabbed app HWND.
/// </summary>
internal sealed class DragWatcher : IDisposable
{
    private readonly Native.WinEventDelegate _proc;
    private IntPtr _hook;

    public event Action<IntPtr>? DragStarted;
    public event Action<IntPtr>? DragEnded;

    /// <summary>
    /// When true, DragEnded events are suppressed once. Used by GestureEngine
    /// to swallow the MOVESIZEEND that fires synthetically after we
    /// SendInput an ESC + LBUTTONUP to escape the system's modal move loop.
    /// </summary>
    public bool SuppressNextDragEnded { get; set; }

    public DragWatcher()
    {
        _proc = OnWinEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_MOVESIZESTART,
            Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero,
            _proc,
            idProcess: 0,
            idThread: 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWinEventHook failed");
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;
        if (hwnd == IntPtr.Zero) return;

        // Resolve to the real movable top-level window. Critical for Groupy:
        // the tab host is GA_ROOT, the inner tabbed app sits below it.
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        switch (eventType)
        {
            case Native.EVENT_SYSTEM_MOVESIZESTART:
                DragStarted?.Invoke(root);
                break;
            case Native.EVENT_SYSTEM_MOVESIZEEND:
                if (SuppressNextDragEnded)
                {
                    SuppressNextDragEnded = false;
                    return;
                }
                DragEnded?.Invoke(root);
                break;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
