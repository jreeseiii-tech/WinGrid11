using System.Windows.Interop;
using WinGrid11.Win32;

namespace WinGrid11.Input;

/// <summary>
/// Registers a single global hotkey via Win32 RegisterHotKey and raises
/// <see cref="Pressed"/> whenever the user hits it.
///
/// Replaces the earlier WH_KEYBOARD_LL hook for the panic-reset gesture.
/// The OS-level hotkey API only delivers the single registered chord -
/// the process never sees any other keystroke - which removes the
/// keylogger-shaped capability that low-level keyboard hooks have. It's
/// also friendlier to AV/EDR heuristics that flag LL keyboard hooks.
///
/// MOD_NOREPEAT means we get exactly one event when the chord is
/// pressed, not a stream while it's held - matches how a "panic reset"
/// should feel.
/// </summary>
internal sealed class HotKeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    public event Action? Pressed;

    public HotKeyManager(uint virtualKey, bool ctrl, bool alt, bool shift, int id = 0xC0DE)
    {
        _id = id;

        // Message-only window (parent = HWND_MESSAGE = -3): never visible,
        // never enumerated, exists solely to receive WM_HOTKEY. Keeps the
        // hotkey decoupled from the visible UI lifecycle (overlays come
        // and go; the hotkey window stays put for the app's lifetime).
        var p = new HwndSourceParameters("WinGrid11.HotKey")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        uint mods = Native.MOD_NOREPEAT;
        if (ctrl) mods |= Native.MOD_CONTROL;
        if (alt) mods |= Native.MOD_ALT;
        if (shift) mods |= Native.MOD_SHIFT;

        _registered = Native.RegisterHotKey(_source.Handle, _id, mods, virtualKey);
        // Failure here usually means another app has the same combo
        // registered. We don't throw - the panic reset is also reachable
        // via the tray menu, so a missing hotkey is degraded but usable.
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == _id)
        {
            try { Pressed?.Invoke(); }
            catch { /* never let an exception escape the WndProc */ }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
