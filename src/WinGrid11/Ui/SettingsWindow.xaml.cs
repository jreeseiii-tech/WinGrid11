using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Microsoft.Win32;
using WinGrid11.Display;
using WinGrid11.Gesture;
using WinGrid11.Overlay;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using VerticalAlignment = System.Windows.VerticalAlignment;

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

        BuildMonitorList();

        // Mirror monitor hot-plug into the per-monitor section while the
        // settings window is open. Fires on a worker thread, so the
        // handler marshals back to the dispatcher before touching WPF.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(BuildMonitorList);
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
        // Monitors without an override display the global default in
        // their sliders; keep them in sync.
        BuildMonitorList();
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
        BuildMonitorList();
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

    /// <summary>
    /// Repopulate the PER-MONITOR GRID section from the currently
    /// connected monitors. Called on construction, after the global
    /// default changes (so default-tracking rows refresh their display
    /// value), and on DisplaySettingsChanged.
    /// </summary>
    private void BuildMonitorList()
    {
        MonitorsList.Children.Clear();

        var monitors = MonitorService.Enumerate();
        int index = 1;
        foreach (var mon in monitors)
        {
            MonitorsList.Children.Add(BuildMonitorRow(mon, index));
            index++;
        }

        if (monitors.Count == 0)
        {
            MonitorsList.Children.Add(new TextBlock
            {
                Text = "No monitors detected.",
                Foreground = (Brush)Resources["MutedBrush"],
                FontStyle = FontStyles.Italic,
            });
        }
    }

    private UIElement BuildMonitorRow(MonitorInfo mon, int displayIndex)
    {
        bool hasOverride = _settings.MonitorOverrides.TryGetValue(mon.DeviceId, out var ov);
        int cols = hasOverride ? ov!.Columns : _settings.Columns;
        int rows = hasOverride ? ov!.Rows : _settings.Rows;

        var muted = (Brush)Resources["MutedBrush"];
        var border = (Brush)Resources["BorderBrush"];

        var card = new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var stack = new StackPanel();
        card.Child = stack;

        // Top line: "Monitor N: <friendly>  [default]"
        var header = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.Inlines.Add(new Run($"Monitor {displayIndex}: {mon.FriendlyName}"));
        var defaultLabel = new Run("  default")
        {
            Foreground = muted,
            FontWeight = FontWeights.Normal,
            FontStyle = FontStyles.Italic,
        };
        header.Inlines.Add(defaultLabel);
        defaultLabel.Text = hasOverride ? "" : "  default";
        stack.Children.Add(header);

        // Sub-line: resolution and position, separated from the name so
        // long monitor friendly names don't push it off the right edge.
        stack.Children.Add(new TextBlock
        {
            Text = $"{mon.PhysicalBounds.Width}\u00D7{mon.PhysicalBounds.Height} @ ({mon.PhysicalBounds.Left}, {mon.PhysicalBounds.Top})",
            Foreground = muted,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var colsValueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = cols.ToString(),
        };
        var rowsValueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = rows.ToString(),
        };
        var colsSlider = MakeGridSlider(cols);
        var rowsSlider = MakeGridSlider(rows);

        stack.Children.Add(MakeSliderRow("Columns", colsSlider, colsValueText));
        stack.Children.Add(MakeSliderRow("Rows", rowsSlider, rowsValueText));

        var resetButton = new Button
        {
            Content = "Reset",
            Width = 80,
            IsEnabled = hasOverride,
            ToolTip = "Remove this monitor's override and use the default grid size.",
        };

        // Suppress events during the initial Value assignment above so we
        // don't immediately register an override on row construction.
        // Tracks per-row to avoid clobbering when multiple rows exist.
        bool initializing = true;

        void Sync()
        {
            if (initializing) return;
            int c = (int)Math.Round(colsSlider.Value);
            int r = (int)Math.Round(rowsSlider.Value);
            colsValueText.Text = c.ToString();
            rowsValueText.Text = r.ToString();

            if (c == _settings.Columns && r == _settings.Rows)
            {
                // Slider values match the global default - clear any
                // existing override so the monitor tracks the default
                // going forward instead of being frozen at this value.
                if (_settings.MonitorOverrides.Remove(mon.DeviceId))
                {
                    defaultLabel.Text = "  default";
                    resetButton.IsEnabled = false;
                    Persist();
                    _overlays.Refresh();
                }
                return;
            }

            _settings.MonitorOverrides[mon.DeviceId] = new MonitorGridSize { Columns = c, Rows = r };
            defaultLabel.Text = "";
            resetButton.IsEnabled = true;
            Persist();
            _overlays.Refresh();
        }

        colsSlider.ValueChanged += (_, _) => Sync();
        rowsSlider.ValueChanged += (_, _) => Sync();

        resetButton.Click += (_, _) =>
        {
            if (!_settings.MonitorOverrides.Remove(mon.DeviceId)) return;
            Persist();
            _overlays.Refresh();
            // Snap sliders back to the global default visually, without
            // triggering the change handler (which would re-create the
            // override at the same value).
            initializing = true;
            colsSlider.Value = _settings.Columns;
            rowsSlider.Value = _settings.Rows;
            colsValueText.Text = _settings.Columns.ToString();
            rowsValueText.Text = _settings.Rows.ToString();
            defaultLabel.Text = "  default";
            resetButton.IsEnabled = false;
            initializing = false;
        };

        var resetDock = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(resetButton, Dock.Right);
        resetDock.Children.Add(resetButton);
        // Spacer text so the DockPanel's LastChildFill doesn't stretch the button.
        resetDock.Children.Add(new TextBlock());
        stack.Children.Add(resetDock);

        initializing = false;
        return card;
    }

    private static Slider MakeGridSlider(int initial)
    {
        return new Slider
        {
            Minimum = 1,
            Maximum = 32,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 2,
            Value = initial,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
    }

    private static UIElement MakeSliderRow(string label, Slider slider, TextBlock valueText)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);

        grid.Children.Add(labelText);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        return grid;
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
