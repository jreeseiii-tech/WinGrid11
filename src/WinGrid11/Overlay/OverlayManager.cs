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

    public void Show(bool blockBackgroundInteraction)
    {
        if (_overlays.Count == 0)
            CreateOverlays();

        foreach (var w in _overlays)
        {
            w.Clear();
            // SetClickThrough must be called after the HWND exists - show
            // first if needed, then toggle. WPF's Show() pumps SourceInitialized
            // synchronously so the HWND is available right after.
            if (!w.IsVisible) w.Show();
            w.SetClickThrough(!blockBackgroundInteraction);
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
            var w = new GridOverlayWindow(mon, _settings.Columns, _settings.Rows, cellColor, hiColor, strokeColor);
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
