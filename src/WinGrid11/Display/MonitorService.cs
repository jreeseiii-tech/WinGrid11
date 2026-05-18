using System.Runtime.InteropServices;
using WinGrid11.Win32;

namespace WinGrid11.Display;

/// <summary>
/// Per-monitor metrics used by the gesture and overlay layers.
/// PhysicalBounds is the full monitor in physical pixels; WorkArea is
/// the same minus the taskbar and any other appbars. The grid is
/// laid out in WorkArea so snaps don't tuck windows under the
/// taskbar; PhysicalBounds is still used to resolve which monitor
/// the cursor is on (so a cursor hovering over the taskbar keeps
/// the gesture alive on the right monitor).
///
/// DeviceId is the EDID-derived hardware device path from
/// EnumDisplayDevices (\\?\DISPLAY#...). It's stable per physical
/// monitor across reconnects and DISPLAYn-shuffles, which makes it the
/// right key for per-monitor settings. FriendlyName is the marketing
/// string Windows surfaces (e.g. "LG ULTRAWIDE"); falls back to the
/// adapter name when EDID isn't available.
/// </summary>
internal readonly record struct MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    string DeviceId,
    string FriendlyName,
    Native.RECT PhysicalBounds,
    Native.RECT WorkArea,
    uint DpiX,
    uint DpiY)
{
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;

    public bool Contains(int x, int y) =>
        x >= PhysicalBounds.Left && x < PhysicalBounds.Right &&
        y >= PhysicalBounds.Top && y < PhysicalBounds.Bottom;
}

internal static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        Native.MonitorEnumProc proc = (IntPtr hMon, IntPtr _, ref Native.RECT __, IntPtr ___) =>
        {
            var mi = new Native.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<Native.MONITORINFOEX>();
            if (!Native.GetMonitorInfoW(hMon, ref mi))
                return true;

            uint dpiX = 96, dpiY = 96;
            Native.GetDpiForMonitor(hMon, Native.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);

            // Resolve a stable hardware ID for this monitor. szDevice
            // ("\\.\DISPLAY1") is the adapter and can renumber across
            // reconnects; the child display under that adapter exposes
            // an EDID-derived device path that doesn't.
            string deviceId = mi.szDevice;
            string friendlyName = mi.szDevice;
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (Native.EnumDisplayDevices(mi.szDevice, 0, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                if (!string.IsNullOrEmpty(dd.DeviceID)) deviceId = dd.DeviceID;
                if (!string.IsNullOrEmpty(dd.DeviceString)) friendlyName = dd.DeviceString;
            }

            list.Add(new MonitorInfo(
                hMon,
                mi.szDevice,
                deviceId,
                friendlyName,
                mi.rcMonitor,
                mi.rcWork,
                dpiX,
                dpiY));

            return true;
        };

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    }
}
