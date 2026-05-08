using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using WinGrid11.Gesture;
using WinGrid11.Overlay;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WinGrid11.Ui;

internal partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly OverlayManager _overlays;
    private readonly GestureEngine _engine;
    private readonly Action _restartElevated;
    private readonly Action _exitApp;

    // Suppress change handlers while we initialise control values from
    // the settings object - otherwise every control fires its event on
    // construction and we'd save (and refresh overlays) for no reason.
    private bool _suppressEvents = true;

    public SettingsWindow(
        Settings settings,
        OverlayManager overlays,
        GestureEngine engine,
        bool elevated,
        Action restartElevated,
        Action exitApp)
    {
        InitializeComponent();

        _settings = settings;
        _overlays = overlays;
        _engine = engine;
        _restartElevated = restartElevated;
        _exitApp = exitApp;

        VersionLabel.Text = "v" + GetAppVersion();

        StatusLine.Text = elevated
            ? "Running as administrator. Can manage Task Manager, regedit, and other elevated apps."
            : "Running as standard user. Elevated apps (Task Manager, regedit, etc.) won't be managed. Use Restart as administrator below to enable.";

        ElevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        FreeResizeBox.IsChecked = settings.FreeResize;
        RmbAgainBox.IsChecked = settings.RmbAgainSwitchesToFree;
        LiveResizeBox.IsChecked = settings.LiveResize;
        KeepOnScreenBox.IsChecked = settings.KeepWindowOnScreen;
        BlockInteractionBox.IsChecked = settings.BlockBackgroundInteraction;
        AutoStartBox.IsChecked = AutoStartManager.IsEnabled;
        UpdateRmbAgainEnabled();

        ColumnsSlider.Value = settings.Columns;
        RowsSlider.Value = settings.Rows;
        ColumnsValue.Text = settings.Columns.ToString();
        RowsValue.Text = settings.Rows.ToString();

        CellColorBox.Text = settings.CellColor;
        HighlightColorBox.Text = settings.HighlightColor;
        StrokeColorBox.Text = settings.StrokeColor;
        UpdateColorSwatches();

        _suppressEvents = false;
    }

    private void OnBoolToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.FreeResize = FreeResizeBox.IsChecked == true;
        _settings.RmbAgainSwitchesToFree = RmbAgainBox.IsChecked == true;
        _settings.LiveResize = LiveResizeBox.IsChecked == true;
        _settings.KeepWindowOnScreen = KeepOnScreenBox.IsChecked == true;
        _settings.BlockBackgroundInteraction = BlockInteractionBox.IsChecked == true;
        UpdateRmbAgainEnabled();
        Persist();
    }

    /// <summary>
    /// Re-syncs the boolean checkboxes from the live Settings object.
    /// Called by the tray when its quick-toggle items change settings
    /// while this window is open, so both surfaces stay in agreement.
    /// </summary>
    public void RefreshFromSettings()
    {
        _suppressEvents = true;
        try
        {
            FreeResizeBox.IsChecked = _settings.FreeResize;
            RmbAgainBox.IsChecked = _settings.RmbAgainSwitchesToFree;
            LiveResizeBox.IsChecked = _settings.LiveResize;
            KeepOnScreenBox.IsChecked = _settings.KeepWindowOnScreen;
            BlockInteractionBox.IsChecked = _settings.BlockBackgroundInteraction;
            UpdateRmbAgainEnabled();
        }
        finally { _suppressEvents = false; }
    }

    /// <summary>
    /// The RMB-again switch only matters in grid mode; grey it out when
    /// the user is already running free resize so the inert state is
    /// visible and the tooltip explains why.
    /// </summary>
    private void UpdateRmbAgainEnabled()
    {
        bool free = FreeResizeBox.IsChecked == true;
        RmbAgainBox.IsEnabled = !free;
        RmbAgainBox.ToolTip = free
            ? "Already in free resize mode. This setting only applies when grid mode is active."
            : "In grid mode, click RMB again after the grid appears to finish the gesture in free resize, anchored at the grid's start cell.";
    }

    private void OnGridChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        int cols = (int)Math.Round(ColumnsSlider.Value);
        int rows = (int)Math.Round(RowsSlider.Value);
        if (cols == _settings.Columns && rows == _settings.Rows) return;

        _settings.Columns = cols;
        _settings.Rows = rows;
        ColumnsValue.Text = cols.ToString();
        RowsValue.Text = rows.ToString();
        Persist();
        // Grid dimensions changed - rebuild the per-monitor overlays so
        // the next gesture uses the new layout.
        _overlays.Refresh();
    }

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.CellColor = CellColorBox.Text;
        _settings.HighlightColor = HighlightColorBox.Text;
        _settings.StrokeColor = StrokeColorBox.Text;
        UpdateColorSwatches();
        Persist();
        _overlays.Refresh();
    }

    private void UpdateColorSwatches()
    {
        CellSwatch.Background = ParseBrush(CellColorBox.Text);
        HighlightSwatch.Background = ParseBrush(HighlightColorBox.Text);
        StrokeSwatch.Background = ParseBrush(StrokeColorBox.Text);
    }

    private static Brush ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            return new SolidColorBrush(color);
        }
        catch
        {
            // Diagonal-stripe pattern would be cleaner, but a flat
            // mid-grey is sufficient signal that the hex didn't parse.
            return new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A));
        }
    }

    private void Persist()
    {
        try { _settings.Save(); } catch { /* I/O blip; in-memory state is still consistent */ }
    }

    private void OnResetGrid(object sender, RoutedEventArgs e)
    {
        // Defaults match the new-Settings constructor - keep them in sync
        // if the defaults ever change there.
        _suppressEvents = true;
        ColumnsSlider.Value = 12;
        RowsSlider.Value = 6;
        ColumnsValue.Text = "12";
        RowsValue.Text = "6";
        _suppressEvents = false;

        _settings.Columns = 12;
        _settings.Rows = 6;
        Persist();
        _overlays.Refresh();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool desired = AutoStartBox.IsChecked == true;
        bool ok = AutoStartManager.SetEnabled(desired);
        if (!ok)
        {
            // Roll the checkbox back to the actual registry state if the
            // write failed - better than showing a lying check state.
            _suppressEvents = true;
            AutoStartBox.IsChecked = AutoStartManager.IsEnabled;
            _suppressEvents = false;
        }
    }

    /// <summary>
    /// Prefer InformationalVersion (the SemVer string from csproj
    /// &lt;Version&gt;) over AssemblyVersion (4-part numeric). Strip any
    /// "+commitsha" suffix that .NET 8 may append in source-link builds.
    /// </summary>
    private static string GetAppVersion()
    {
        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var v = info ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private void OnResetGesture(object sender, RoutedEventArgs e) => _engine.Reset();

    private void OnRestartElevated(object sender, RoutedEventArgs e) => _restartElevated();

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinGrid11");
        try { Process.Start("explorer.exe", dir); } catch { }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => _exitApp();

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // UseShellExecute=true routes to the user's default browser
            // for http(s) URIs. Wrapped in try/catch because explorer/
            // shell can refuse for any number of reasons (no default
            // browser, sandboxed environment, etc.) and we'd rather
            // swallow than crash the settings window.
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch { }
    }
}
