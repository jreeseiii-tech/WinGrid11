using System.IO;
using System.Text.Json;

namespace WinGrid11;

internal sealed class MonitorGridSize
{
    public int Columns { get; set; }
    public int Rows { get; set; }
}

internal sealed class Settings
{
    public int Columns { get; set; } = 12;
    public int Rows { get; set; } = 6;

    /// <summary>
    /// Per-monitor grid size overrides keyed by the monitor's stable
    /// device ID (EDID-derived path from EnumDisplayDevices). When a
    /// monitor has an entry here, its overlay uses those columns/rows
    /// instead of the global default above. Monitors not in this
    /// dictionary fall back to the default.
    /// </summary>
    public Dictionary<string, MonitorGridSize> MonitorOverrides { get; set; } = new();
    public string CellColor { get; set; } = "#4080FF";
    public string HighlightColor { get; set; } = "#40C0FF";
    public string StrokeColor { get; set; } = "#80C0FF";

    /// <summary>
    /// When true, the target window streams to follow the selected cell
    /// range during the gesture (the original WindowGrid behaviour). When
    /// false, the window stays put and is committed once on LBUTTONUP.
    ///
    /// Default is false because the streaming mode triggers content
    /// corruption in apps with custom rendering pipelines (Electron/
    /// Chromium - Cursor/VS Code; FilePilot). Snap-on-release mimics the
    /// final committed state of a manual resize and works with all apps.
    /// </summary>
    public bool LiveResize { get; set; } = false;

    /// <summary>
    /// When true, after the snap commits we check whether the window's
    /// minimum size pushed it past the monitor work area and shift it
    /// back on-screen if so. Useful when picking cells along the right
    /// or bottom edges with windows that have a hard minimum width/height
    /// larger than the chosen cell range. When false, the original
    /// WindowGrid behaviour is preserved (windows can extend past the
    /// edge if their min size is too large).
    /// </summary>
    public bool KeepWindowOnScreen { get; set; } = true;

    /// <summary>
    /// When true, the grid overlay catches mouse events during the gesture
    /// (we clear WS_EX_TRANSPARENT on it) so the cursor can't hover/click
    /// through to apps under the cursor - particularly relevant in
    /// snap-on-release mode where the target window doesn't follow the
    /// cursor. The LL hook still drives the gesture either way; this only
    /// changes whether apps below the overlay see the cursor at all.
    /// Apps with an active modal sizing/move loop (Electron/FilePilot
    /// drags) still receive their events via SetCapture regardless of
    /// this setting.
    /// </summary>
    public bool BlockBackgroundInteraction { get; set; } = true;

    /// <summary>
    /// When true, the gesture picks an arbitrary pixel-precise rectangle
    /// (start point = where RMB was pressed, end point = wherever the
    /// cursor is on LBUTTONUP) instead of snapping to grid cells. The
    /// overlay shows a single preview rectangle from start to current
    /// cursor instead of cell highlights. Same hook/cancel/commit
    /// machinery as grid mode; only the rect computation changes.
    /// </summary>
    public bool FreeResize { get; set; } = false;

    /// <summary>
    /// When true and grid mode is active, a second RMB press during the
    /// gesture switches the rest of the gesture to free resize. The
    /// grid's start cell anchors the free rect; the cursor drives the
    /// end point until LBUTTONUP. Useful when the user wants to fine-tune
    /// the cell-aligned selection to pixel precision without reopening
    /// settings.
    /// </summary>
    public bool RmbAgainSwitchesToFree { get; set; } = false;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinGrid11");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is not null) return s.Validate();
            }
        }
        catch { /* fall through to default */ }

        var def = new Settings();
        try { def.Save(); } catch { }
        return def;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    private Settings Validate()
    {
        Columns = Math.Clamp(Columns, 1, 64);
        Rows = Math.Clamp(Rows, 1, 64);
        MonitorOverrides ??= new Dictionary<string, MonitorGridSize>();
        foreach (var (_, ov) in MonitorOverrides)
        {
            ov.Columns = Math.Clamp(ov.Columns, 1, 64);
            ov.Rows = Math.Clamp(ov.Rows, 1, 64);
        }
        return this;
    }
}
