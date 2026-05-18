using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WinGrid11.Display;
using WinGrid11.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Visibility = System.Windows.Visibility;

namespace WinGrid11.Overlay;

internal partial class GridOverlayWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly int _cols;
    private readonly int _rows;
    private readonly Rectangle[,] _cells;
    private readonly Brush _cellFill;
    private readonly Brush _highlightFill;
    private readonly Brush _gridStroke;

    private (int col, int row)? _start;
    private (int col, int row)? _end;

    public GridOverlayWindow(MonitorInfo monitor, int cols, int rows, Color cellColor, Color highlightColor, Color strokeColor)
    {
        InitializeComponent();

        _monitor = monitor;
        // Auto-transpose for portrait monitors (matches WindowGrid 1.3.1.0 behavior).
        // Uses WorkArea so a side-mounted taskbar that flips the available
        // ratio is what we react to, not the monitor's physical orientation.
        var wa = monitor.WorkArea;
        bool portrait = wa.Height > wa.Width;
        _cols = portrait ? rows : cols;
        _rows = portrait ? cols : rows;

        _cellFill = new SolidColorBrush(cellColor) { Opacity = 0.20 };
        _cellFill.Freeze();
        _highlightFill = new SolidColorBrush(highlightColor) { Opacity = 0.45 };
        _highlightFill.Freeze();
        _gridStroke = new SolidColorBrush(strokeColor) { Opacity = 0.9 };
        _gridStroke.Freeze();

        _cells = new Rectangle[_cols, _rows];
        BuildGrid();

        // The free-resize preview shares the highlight fill / grid stroke
        // so it visually matches a cell selection.
        FreePreview.Fill = _highlightFill;
        FreePreview.Stroke = _gridStroke;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PositionForMonitor();

        // When the window crosses a DPI boundary (e.g. moves from a 150%
        // monitor to a 100% one), WPF processes WM_DPICHANGED and applies
        // the OS-suggested rect which preserves the window's visual size
        // across the change - so the layered overlay ends up sized
        // 96/144 = 2/3 of the target monitor's work area instead of
        // filling it. Re-apply our intended physical-pixel rect after
        // WPF has updated its internal DPI scaling. Dispatched so it
        // runs after WPF's own WM_DPICHANGED handling completes.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PositionForMonitor));
    }

    public int Cols => _cols;
    public int Rows => _rows;
    public MonitorInfo Monitor => _monitor;

    private void BuildGrid()
    {
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();

        for (int c = 0; c < _cols; c++)
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < _rows; r++)
            RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                var rect = new Rectangle
                {
                    Fill = _cellFill,
                    Stroke = _gridStroke,
                    StrokeThickness = 1,
                    Margin = new Thickness(2),
                    RadiusX = 4,
                    RadiusY = 4,
                };
                Grid.SetColumn(rect, c);
                Grid.SetRow(rect, r);
                RootGrid.Children.Add(rect);
                _cells[c, r] = rect;
            }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Default: click-through and not-activatable. We get input from the
        // LL hook, not from this overlay. The click-through flag may be
        // toggled off during a gesture (see SetClickThrough) to prevent
        // mouse events from leaking through to apps below the overlay.
        var ex = Native.GetWindowLongPtr64(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
              | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr64(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        PositionForMonitor();
    }

    /// <summary>
    /// Toggle WS_EX_TRANSPARENT. With it set the OS skips this window during
    /// mouse hit-testing and events fall through to apps below (the
    /// original WindowGrid behaviour). Cleared, the overlay catches all
    /// mouse events that aren't already routed via SetCapture, blocking
    /// hover/click interaction with apps under the cursor while a gesture
    /// is active. The LL hook still drives the gesture either way - it
    /// fires before message dispatch.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var ex = Native.GetWindowLongPtr64(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if (clickThrough)
            ex |= Native.WS_EX_TRANSPARENT;
        else
            ex &= ~(long)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr64(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void PositionForMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Cover the WorkArea, not the full monitor: keeps the grid out
        // from under the taskbar / appbars so snapped windows can't end
        // up tucked behind them.
        var b = _monitor.WorkArea;

        // Position via SetWindowPos in physical pixels - bypasses WPF's DPI-aware
        // top-level coordinate translation, which is otherwise subtly wrong on
        // multi-DPI setups.
        Native.SetWindowPos(
            hwnd,
            Native.HWND_TOPMOST,
            b.Left, b.Top, b.Width, b.Height,
            Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Map a physical screen pixel point to a grid cell on this monitor.
    /// Returns null only if this overlay isn't the right monitor for
    /// the cursor; a cursor inside the monitor but over the taskbar
    /// clamps to the closest edge cell so the gesture stays usable.
    /// </summary>
    public (int col, int row)? CellFromPhysical(int x, int y)
    {
        if (!_monitor.Contains(x, y)) return null;

        var b = _monitor.WorkArea;
        double fx = (x - b.Left) / (double)b.Width;
        double fy = (y - b.Top) / (double)b.Height;

        int col = Math.Clamp((int)(fx * _cols), 0, _cols - 1);
        int row = Math.Clamp((int)(fy * _rows), 0, _rows - 1);
        return (col, row);
    }

    /// <summary>
    /// Compute the physical-pixel rect spanning cells [start..end] on this monitor.
    /// </summary>
    public Native.RECT RectFromCells((int col, int row) a, (int col, int row) b)
    {
        int c1 = Math.Min(a.col, b.col);
        int c2 = Math.Max(a.col, b.col);
        int r1 = Math.Min(a.row, b.row);
        int r2 = Math.Max(a.row, b.row);

        var bounds = _monitor.WorkArea;
        double cw = bounds.Width / (double)_cols;
        double rh = bounds.Height / (double)_rows;

        int left = bounds.Left + (int)Math.Round(c1 * cw);
        int top = bounds.Top + (int)Math.Round(r1 * rh);
        int right = bounds.Left + (int)Math.Round((c2 + 1) * cw);
        int bottom = bounds.Top + (int)Math.Round((r2 + 1) * rh);

        return new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    public void SetSelection((int col, int row)? start, (int col, int row)? end)
    {
        _start = start;
        _end = end;
        UpdateHighlights();
    }

    public void Clear()
    {
        _start = _end = null;
        UpdateHighlights();
        FreeLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Switch to free-resize mode: hide the cell grid, show an empty
    /// preview layer ready for SetFreePreview.
    /// </summary>
    public void EnterFreeMode()
    {
        RootGrid.Visibility = Visibility.Collapsed;
        FreeLayer.Visibility = Visibility.Visible;
        FreePreview.Width = 0;
        FreePreview.Height = 0;
    }

    /// <summary>
    /// Switch back to grid mode: show cells, hide preview layer.
    /// </summary>
    public void EnterGridMode()
    {
        RootGrid.Visibility = Visibility.Visible;
        FreeLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Position the free-resize preview rectangle from physical screen
    /// coordinates. Converts to DIP coords inside the window using the
    /// monitor's effective DPI. The window itself is anchored to
    /// WorkArea, so canvas (0,0) = WorkArea top-left in physical pixels.
    /// </summary>
    public void SetFreePreview(int physStartX, int physStartY, int physEndX, int physEndY)
    {
        int left = Math.Min(physStartX, physEndX);
        int top = Math.Min(physStartY, physEndY);
        int right = Math.Max(physStartX, physEndX);
        int bottom = Math.Max(physStartY, physEndY);

        var b = _monitor.WorkArea;
        double sx = _monitor.ScaleX;
        double sy = _monitor.ScaleY;

        double canvasLeft = (left - b.Left) / sx;
        double canvasTop = (top - b.Top) / sy;
        double width = (right - left) / sx;
        double height = (bottom - top) / sy;

        Canvas.SetLeft(FreePreview, canvasLeft);
        Canvas.SetTop(FreePreview, canvasTop);
        FreePreview.Width = width;
        FreePreview.Height = height;
    }

    private void UpdateHighlights()
    {
        if (_start is null)
        {
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r].Fill = _cellFill;
            return;
        }

        var s = _start.Value;
        var e = _end ?? s;
        int c1 = Math.Min(s.col, e.col), c2 = Math.Max(s.col, e.col);
        int r1 = Math.Min(s.row, e.row), r2 = Math.Max(s.row, e.row);

        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                bool selected = c >= c1 && c <= c2 && r >= r1 && r <= r2;
                _cells[c, r].Fill = selected ? _highlightFill : _cellFill;
            }
        }
    }
}
