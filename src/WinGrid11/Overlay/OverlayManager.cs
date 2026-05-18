using WinGrid11.Display;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace WinGrid11.Overlay;

internal sealed class OverlayManager : IDisposable
{
    private readonly Settings _settings;
    private readonly List<GridOverlayWindow> _overlays = new();

    public OverlayManager(Settings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<GridOverlayWindow> Overlays => _overlays;

    /// <summary>
    /// Pre-create overlay windows at app startup so the first gesture doesn't
    /// pay the cost of constructing/laying-out 72 rectangles synchronously.
    /// </summary>
    public void Initialize()
    {
        if (_overlays.Count == 0)
            CreateOverlays();
    }

    /// <summary>
    /// Show only the overlay for the target monitor; hide every other one.
    /// The grid follows the cursor across monitors (driven by the gesture
    /// engine), but only the monitor the cursor is currently on shows
    /// the grid - the old behaviour of lighting up every monitor was
    /// noisy on multi-display setups.
    /// </summary>
    public void ShowOnly(GridOverlayWindow target, bool blockBackgroundInteraction)
    {
        if (_overlays.Count == 0) CreateOverlays();

        foreach (var w in _overlays)
        {
            if (ReferenceEquals(w, target))
            {
                w.Clear();
                // SetClickThrough must run after the HWND exists - show
                // first if needed, then toggle. WPF's Show() pumps
                // SourceInitialized synchronously so the HWND is
                // available immediately after.
                if (!w.IsVisible) w.Show();
                w.SetClickThrough(!blockBackgroundInteraction);
            }
            else if (w.IsVisible)
            {
                w.Clear();
                // Restore click-through before hiding so a stale overlay
                // can't briefly intercept input during the hide animation.
                w.SetClickThrough(true);
                w.Hide();
            }
        }
    }

    public void Hide()
    {
        foreach (var w in _overlays)
        {
            w.Clear();
            // Restore click-through before hiding so the next Show() starts
            // from a known state, and so a stray cursor over a still-visible
            // overlay (during a slow Hide() animation, etc.) can't block
            // input.
            w.SetClickThrough(true);
            if (w.IsVisible) w.Hide();
        }
    }

    public void Refresh()
    {
        foreach (var w in _overlays) w.Close();
        _overlays.Clear();
        CreateOverlays();
    }

    private void CreateOverlays()
    {
        var cellColor = ParseColor(_settings.CellColor, Color.FromRgb(0x40, 0x80, 0xFF));
        var hiColor = ParseColor(_settings.HighlightColor, Color.FromRgb(0x40, 0xC0, 0xFF));
        var strokeColor = ParseColor(_settings.StrokeColor, Color.FromRgb(0x80, 0xC0, 0xFF));

        foreach (var mon in MonitorService.Enumerate())
        {
            // Per-monitor override takes precedence over the global
            // default. Missing key -> use defaults.
            int cols = _settings.Columns;
            int rows = _settings.Rows;
            if (_settings.MonitorOverrides.TryGetValue(mon.DeviceId, out var ov))
            {
                cols = ov.Columns;
                rows = ov.Rows;
            }

            var w = new GridOverlayWindow(mon, cols, rows, cellColor, hiColor, strokeColor);
            _overlays.Add(w);
        }
    }

    public GridOverlayWindow? OverlayForPoint(int x, int y)
    {
        foreach (var w in _overlays)
            if (w.Monitor.Contains(x, y))
                return w;
        return null;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    public void Dispose()
    {
        foreach (var w in _overlays) w.Close();
        _overlays.Clear();
    }
}
