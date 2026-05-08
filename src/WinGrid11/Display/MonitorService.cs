using WinGrid11.Win32;

namespace WinGrid11.Display;

internal readonly record struct MonitorInfo(
    IntPtr Handle,
    Native.RECT PhysicalBounds,
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
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>();
            if (!Native.GetMonitorInfoW(hMon, ref mi))
                return true;

            uint dpiX = 96, dpiY = 96;
            Native.GetDpiForMonitor(hMon, Native.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);

            list.Add(new MonitorInfo(
                hMon,
                mi.rcMonitor,
                dpiX,
                dpiY));

            return true;
        };

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    }
}
