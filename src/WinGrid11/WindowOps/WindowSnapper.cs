using System.Runtime.InteropServices;
using WinGrid11.Win32;

namespace WinGrid11.WindowOps;

internal static class WindowSnapper
{
    /// <summary>
    /// Insets (window-rect minus extended-frame-bounds) per side, in the
    /// target window's physical pixels. On Win10/11 these are the invisible
    /// resize borders that make windows look 7-ish px smaller than the real
    /// rectangle returned by GetWindowRect.
    /// </summary>
    public static Native.MARGINS GetInvisibleFrameInsets(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var winRect))
            return default;

        int hr = Native.DwmGetWindowAttribute(
            hwnd,
            Native.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var visRect,
            Marshal.SizeOf<Native.RECT>());

        if (hr < 0 || visRect.IsEmpty)
            return default;

        return new Native.MARGINS
        {
            Left = visRect.Left - winRect.Left,
            Top = visRect.Top - winRect.Top,
            Right = winRect.Right - visRect.Right,
            Bottom = winRect.Bottom - visRect.Bottom,
        };
    }

    /// <summary>
    /// Resolve the actually-movable top-level window for an HWND. For Groupy 2's
    /// tabbed apps this is the tab host, not the inner app window.
    /// </summary>
    public static IntPtr ResolveRoot(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    /// <summary>
    /// Snap <paramref name="hwnd"/> so its visible (post-DWM-frame) rectangle
    /// matches <paramref name="visibleRectPhys"/> in physical screen pixels.
    /// Caller is responsible for resolving GA_ROOT before calling.
    ///
    /// One SetWindowPos. This is the cross-process equivalent of what a
    /// manual resize commits at the end of its modal sizing loop: a
    /// settled bounds change, one WM_WINDOWPOSCHANGING/WM_NCCALCSIZE/
    /// WM_SIZE pass.
    /// </summary>
    /// <param name="sendFrameChangedKick">
    /// If true, follow the bounds change with a second zero-size
    /// SetWindowPos carrying SWP_FRAMECHANGED. This forces a non-client
    /// recalc and full relayout pass - useful for apps that override
    /// WM_NCCALCSIZE (Groupy 2's tab host being the obvious case) and
    /// only fully relayout their inner content in response. Skip for
    /// snap-on-release mode: a single SetWindowPos is the whole story
    /// there and apps with fragile renderers prefer the lack of a
    /// double-commit.
    /// </param>
    /// <param name="keepOnScreen">
    /// If true, after the snap commits, query the actual window rect and
    /// shift it back into the monitor work area when the window's
    /// minimum size pushed it off the right/bottom edge. Costs an extra
    /// SetWindowPos, but only when the window overflows.
    /// </param>
    public static void SnapToVisibleRect(IntPtr hwnd, Native.RECT visibleRectPhys, bool sendFrameChangedKick = false, bool keepOnScreen = false)
    {
        if (!Native.IsWindow(hwnd)) return;

        if (Native.IsZoomed(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var insets = GetInvisibleFrameInsets(hwnd);

        int x = visibleRectPhys.Left - insets.Left;
        int y = visibleRectPhys.Top - insets.Top;
        int w = visibleRectPhys.Width + insets.Left + insets.Right;
        int h = visibleRectPhys.Height + insets.Top + insets.Bottom;

        // Allow WM_WINDOWPOSCHANGING through. Groupy 2's tab host uses it
        // to relayout its inner tabbed app HWND; suppressing leaves
        // content offset.
        Native.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        if (sendFrameChangedKick)
        {
            Native.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        if (keepOnScreen)
            ShiftIntoWorkArea(hwnd, visibleRectPhys);
    }

    /// <summary>
    /// If the window's actual rect (after Win32 clamped to its min size)
    /// extends past the work area of the monitor that the user selected
    /// cells on, shift it back. We anchor to the monitor of the chosen
    /// cell rect - not MonitorFromWindow - because an oversized window
    /// would otherwise straddle two monitors and we'd clamp to the wrong
    /// one. Only emits a second SetWindowPos when an actual shift is
    /// needed; the no-op case is free.
    /// </summary>
    private static void ShiftIntoWorkArea(IntPtr hwnd, Native.RECT chosenRectPhys)
    {
        if (!Native.GetWindowRect(hwnd, out var actual)) return;

        var insets = GetInvisibleFrameInsets(hwnd);
        var actualVisible = new Native.RECT
        {
            Left = actual.Left + insets.Left,
            Top = actual.Top + insets.Top,
            Right = actual.Right - insets.Right,
            Bottom = actual.Bottom - insets.Bottom,
        };

        var center = new Native.POINT
        {
            X = (chosenRectPhys.Left + chosenRectPhys.Right) / 2,
            Y = (chosenRectPhys.Top + chosenRectPhys.Bottom) / 2,
        };
        var hMon = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfoW(hMon, ref mi)) return;
        var work = mi.rcWork;

        int dx = 0, dy = 0;
        // Push left/up first so right/bottom edges are inside the work
        // area; then make sure left/top haven't fallen off the other side.
        // If the window is wider/taller than the work area, left/top
        // wins (we'd rather see the title bar than the bottom-right
        // corner).
        if (actualVisible.Right > work.Right) dx = work.Right - actualVisible.Right;
        if (actualVisible.Bottom > work.Bottom) dy = work.Bottom - actualVisible.Bottom;
        if (actualVisible.Left + dx < work.Left) dx = work.Left - actualVisible.Left;
        if (actualVisible.Top + dy < work.Top) dy = work.Top - actualVisible.Top;

        if (dx == 0 && dy == 0) return;

        Native.SetWindowPos(
            hwnd, IntPtr.Zero,
            actual.Left + dx, actual.Top + dy, 0, 0,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }
}
