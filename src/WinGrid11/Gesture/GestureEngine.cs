using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinGrid11.Input;
using WinGrid11.Overlay;
using WinGrid11.Win32;
using WinGrid11.WindowOps;

namespace WinGrid11.Gesture;

/// <summary>
/// State machine driving the grid-snap gesture:
///
/// Idle
///   |  DragWatcher.DragStarted (MOVESIZESTART)
///   v
/// Dragging  (LMB held by user, system is in modal move loop)
///   |  RMB down  -> SendInput VK_ESCAPE + synthetic LBUTTONUP to break
///   |              the target's drag (works for both DefWindowProc-based
///   |              modal loops and custom drag implementations).
///   v
/// PickingStart  (overlay shown; cursor selects start cell)
///   |  RMB up
///   v
/// PickingEnd  (overlay highlight tracks cursor; window optionally
///              follows in live-resize mode - see Settings.LiveResize)
///   |  LMB up
///   v
/// Idle  (final SetWindowPos applied to chosen cell rect, overlays hidden)
///
/// Two resize modes (selected by Settings.LiveResize):
///
///  * Snap-on-release (default): the window stays put during the gesture
///    and is committed once on LBUTTONUP. Mimics the final state of a
///    manual resize. Required for apps with custom rendering pipelines
///    (Electron/Chromium - Cursor/VS Code; FilePilot) which corrupt when
///    bombarded with cross-process SetWindowPos calls.
///
///  * Live preview: the window streams to follow the selected cell range
///    on every cell change, and a SWP_FRAMECHANGED kick is added on the
///    final commit (helps Groupy 2's tab host settle after streaming).
///    Original WindowGrid behaviour. Visually richer but incompatible
///    with the fragile renderers above.
///
/// Concurrency notes:
///  * The LL hook callback runs on the WPF dispatcher thread (the same thread
///    that installed the hook). State transitions are therefore done
///    synchronously inside the hook callback so subsequent mouse events
///    classify against the correct state without a race.
///  * Heavy work (Show/Hide overlay windows, SetWindowPos, posting messages
///    to other processes) is dispatched via BeginInvoke so the hook proc
///    returns within the LowLevelHooksTimeout budget (default 300 ms).
/// </summary>
internal sealed class GestureEngine : IDisposable
{
    private enum State { Idle, Dragging, PickingStart, PickingEnd }

    private readonly LowLevelMouseHook _mouse;
    private readonly DragWatcher _drag;
    private readonly OverlayManager _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly Settings _settings;

    private State _state = State.Idle;
    private IntPtr _target;
    private GridOverlayWindow? _activeOverlay;

    // Captured at gesture start from Settings.FreeResize. Doesn't change
    // mid-gesture even if the user toggles the setting.
    private bool _freeMode;

    // Grid mode state.
    private (int col, int row) _startCell;
    private (int col, int row) _endCell;
    // Live mode only: last cell range we issued a SetWindowPos for -
    // throttles to per-cell-change.
    private (int col, int row)? _lastResizedCell;

    // Free mode state. Physical screen pixels.
    private (int X, int Y) _freeStart;
    private (int X, int Y) _freeEnd;

    // Latest-only throttling for mouse moves. The hook records the most
    // recent cursor position; the dispatcher continuation reads whatever is
    // there at the time it runs. A burst of moves coalesces into a single
    // SetWindowPos.
    private int _pendingMoveX;
    private int _pendingMoveY;
    private bool _movePending;

    public GestureEngine(LowLevelMouseHook mouse, DragWatcher drag, OverlayManager overlays, Settings settings, Dispatcher dispatcher)
    {
        _mouse = mouse;
        _drag = drag;
        _overlays = overlays;
        _settings = settings;
        _dispatcher = dispatcher;

        _drag.DragStarted += hwnd => _dispatcher.BeginInvoke(() => OnDragStarted(hwnd));
        _drag.DragEnded += hwnd => _dispatcher.BeginInvoke(() => OnDragEnded(hwnd));

        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;
    }

    private void OnDragStarted(IntPtr hwnd)
    {
        if (_state != State.Idle) return;
        if (!Native.IsWindow(hwnd)) return;

        _target = hwnd;
        _state = State.Dragging;
    }

    private void OnDragEnded(IntPtr hwnd)
    {
        // The user released LMB outside of any gesture (just a normal drag),
        // or our gesture finished naturally. Reset.
        if (_state == State.Idle) return;
        ResetToIdle();
    }

    private void OnMouseDown(object? sender, MouseHookEventArgs e)
    {
        switch (_state)
        {
            case State.Dragging when e.Button == MouseButton.Right:
                if (TryEnterPickingStart(e.X, e.Y))
                    e.Handled = true;
                break;

            // Second RMB during grid mode: opt-in switch to free resize.
            // Anchored at the grid start cell so the user keeps the shape
            // they were building and can fine-tune the end pixel.
            case State.PickingEnd when e.Button == MouseButton.Right
                                       && _settings.RmbAgainSwitchesToFree
                                       && !_freeMode:
                SwitchToFreeMode(e.X, e.Y);
                e.Handled = true;
                break;

            case State.PickingStart:
            case State.PickingEnd:
                e.Handled = true;
                break;
        }
    }

    private void OnMouseUp(object? sender, MouseHookEventArgs e)
    {
        switch (_state)
        {
            case State.PickingStart when e.Button == MouseButton.Right:
                // Transition into "selecting end cell". In snap-on-release
                // mode no SetWindowPos happens until LBUTTONUP - that's
                // the safe path for apps with fragile rendering pipelines.
                // In live mode we kick off the first resize here so the
                // window snaps to the start cell immediately.
                _state = State.PickingEnd;
                e.Handled = true;
                if (_settings.LiveResize)
                {
                    _lastResizedCell = null;
                    ScheduleLiveResize();
                }
                break;

            case State.PickingStart when e.Button == MouseButton.Left:
                // CRITICAL: do NOT swallow LBUTTONUP. For apps that ignored
                // our injected ESC/LBUTTONUP at gesture start (Electron/
                // Chromium, FilePilot, anything with custom-implemented
                // drag tracking on HTCAPTION), the user's physical release
                // is the only remaining signal that ends their drag loop.
                // Eating it here causes the window to stay attached to the
                // cursor until the next click.
                _state = State.Idle;
                _dispatcher.BeginInvoke(ResetToIdle);
                break;

            case State.PickingEnd when e.Button == MouseButton.Left:
                // Same reason as PickingStart cancel: let LBUTTONUP propagate.
                _state = State.Idle;
                CommitFinalSnap(e.X, e.Y);
                break;

            // Right/middle button releases during the gesture: still swallow,
            // we don't want them leaking to apps under the cursor.
            case State.PickingStart:
            case State.PickingEnd:
                e.Handled = true;
                break;
        }
    }

    private void OnMouseMove(object? sender, MouseHookEventArgs e)
    {
        if (_state != State.PickingStart && _state != State.PickingEnd) return;

        // CRITICAL: do NOT mark mouse-move events as Handled. Returning a
        // non-zero value from a low-level mouse hook for WM_MOUSEMOVE on
        // Windows freezes the cursor visually - the cursor visualisation
        // is driven through the same message flow that the hook gates.
        // Apps under the cursor will see the moves, but with the modal
        // sizing loop already cancelled (ESC at PickingStart entry) and
        // LBUTTONDOWN events still swallowed in OnMouseDown, no app can
        // interpret the movement as a drag-in-progress.

        _pendingMoveX = e.X;
        _pendingMoveY = e.Y;

        // Coalesce: only enqueue once; the continuation reads the latest x/y.
        if (_movePending) return;
        _movePending = true;
        _dispatcher.BeginInvoke(DispatchPendingMove);
    }

    private void DispatchPendingMove()
    {
        _movePending = false;
        if (_state != State.PickingStart && _state != State.PickingEnd) return;
        HandleMove(_pendingMoveX, _pendingMoveY);
    }

    /// <summary>
    /// Synchronous transition into PickingStart. Resolves the overlay and
    /// initial selection (cell or pixel point) before returning so
    /// subsequent mouse events have valid state to classify against.
    /// </summary>
    private bool TryEnterPickingStart(int x, int y)
    {
        var overlay = _overlays.OverlayForPoint(x, y);
        if (overlay is null) return false;

        _freeMode = _settings.FreeResize;

        if (_freeMode)
        {
            _freeStart = (x, y);
            _freeEnd = (x, y);
        }
        else
        {
            var cell = overlay.CellFromPhysical(x, y);
            if (cell is null) return false;
            _startCell = cell.Value;
            _endCell = cell.Value;
        }

        _activeOverlay = overlay;
        _state = State.PickingStart;

        // Heavy work off the hook thread.
        _dispatcher.BeginInvoke(() =>
        {
            _drag.SuppressNextDragEnded = true;

            // Try to cancel whatever drag mechanism the target is running:
            //   * ESC ends DefWindowProc's standard modal sizing loop
            //     (Win32 apps with normal chrome - Notepad++, Explorer, etc.)
            //   * synthetic LBUTTONUP ends drag tracking in apps that
            //     implement their own loop on top of WM_NCLBUTTONDOWN
            //     (Electron/Chromium - Cursor, VS Code; FilePilot;
            //     anything with custom-drawn title bars)
            // Whichever applies wins; the other is mostly inert. Apps that
            // ignore both still get cleaned up because we no longer swallow
            // the user's physical LBUTTONUP at gesture end.
            CancelDragOperation();

            _overlays.Show(_settings.BlockBackgroundInteraction);
            if (_freeMode)
            {
                overlay.EnterFreeMode();
                overlay.SetFreePreview(_freeStart.X, _freeStart.Y, _freeEnd.X, _freeEnd.Y);
            }
            else
            {
                overlay.EnterGridMode();
                overlay.SetSelection(_startCell, _startCell);
            }
        });

        return true;
    }

    private void HandleMove(int x, int y)
    {
        if (_state != State.PickingStart && _state != State.PickingEnd) return;

        if (_freeMode)
        {
            HandleFreeMove(x, y);
            return;
        }

        HandleGridMove(x, y);
    }

    private void HandleGridMove(int x, int y)
    {
        var overlay = _overlays.OverlayForPoint(x, y);
        if (overlay is null) return;

        if (!ReferenceEquals(overlay, _activeOverlay))
        {
            _activeOverlay?.Clear();
            _activeOverlay = overlay;
            overlay.EnterGridMode();
            // Re-anchor the start cell on the new monitor - the gesture
            // logically restarts when the user crosses a monitor boundary.
            _startCell = overlay.CellFromPhysical(x, y) ?? _startCell;
            _lastResizedCell = null;
        }

        var endCell = overlay.CellFromPhysical(x, y);
        if (endCell is null) return;
        _endCell = endCell.Value;

        overlay.SetSelection(_startCell, _endCell);

        // Live-resize mode only. Throttled to per-cell-change to keep the
        // SetWindowPos rate reasonable for apps with custom layout pipelines.
        // In snap-on-release mode the overlay highlight is the only preview
        // and the window doesn't move until LBUTTONUP - see CommitFinalSnap.
        if (_settings.LiveResize && _state == State.PickingEnd && _lastResizedCell != _endCell)
        {
            _lastResizedCell = _endCell;
            ScheduleLiveResize();
        }
    }

    /// <summary>
    /// Mid-gesture transition from grid mode to free resize. Triggered by
    /// a second RMB press when Settings.RmbAgainSwitchesToFree is on.
    /// Free start anchors at the top-left of the grid's start cell so
    /// the rect being built so far is preserved; free end is the cursor
    /// at the moment of the switch.
    /// </summary>
    private void SwitchToFreeMode(int x, int y)
    {
        var overlay = _activeOverlay;
        if (overlay is null) return;

        var startRect = overlay.RectFromCells(_startCell, _startCell);
        _freeMode = true;
        _freeStart = (startRect.Left, startRect.Top);
        _freeEnd = (x, y);

        _dispatcher.BeginInvoke(() =>
        {
            overlay.EnterFreeMode();
            overlay.SetFreePreview(_freeStart.X, _freeStart.Y, _freeEnd.X, _freeEnd.Y);
        });
    }

    private void HandleFreeMove(int x, int y)
    {
        // Re-anchor on monitor crossing - same logic as grid mode: the
        // gesture restarts on the new monitor with its start at the
        // current cursor position. Without this, the preview rect would
        // remain stuck on the old monitor while the cursor is elsewhere.
        var newOverlay = _overlays.OverlayForPoint(x, y);
        if (newOverlay is not null && !ReferenceEquals(newOverlay, _activeOverlay))
        {
            _activeOverlay?.Clear();
            _activeOverlay = newOverlay;
            newOverlay.EnterFreeMode();
            _freeStart = (x, y);
        }

        var overlay = _activeOverlay;
        if (overlay is null) return;
        var b = overlay.Monitor.PhysicalBounds;
        int cx = Math.Clamp(x, b.Left, b.Right - 1);
        int cy = Math.Clamp(y, b.Top, b.Bottom - 1);
        _freeEnd = (cx, cy);

        overlay.SetFreePreview(_freeStart.X, _freeStart.Y, cx, cy);

        // No per-cell throttling here - the dispatcher's latest-only
        // coalescing in OnMouseMove already caps the SetWindowPos rate.
        if (_settings.LiveResize && _state == State.PickingEnd)
            ScheduleLiveResize();
    }

    /// <summary>
    /// Compute the target window rect (physical pixels) from the current
    /// mode's selection state. Returns null when the gesture has nothing
    /// resolved yet.
    /// </summary>
    private Native.RECT? ComputeTargetRect()
    {
        var overlay = _activeOverlay;
        if (overlay is null) return null;

        if (_freeMode)
        {
            int left = Math.Min(_freeStart.X, _freeEnd.X);
            int top = Math.Min(_freeStart.Y, _freeEnd.Y);
            int right = Math.Max(_freeStart.X, _freeEnd.X);
            int bottom = Math.Max(_freeStart.Y, _freeEnd.Y);
            // Guarantee a non-degenerate rect; Win32 will clamp up to
            // the window's actual minimum anyway.
            if (right == left) right = left + 1;
            if (bottom == top) bottom = top + 1;
            return new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }

        return overlay.RectFromCells(_startCell, _endCell);
    }

    /// <summary>
    /// Streaming intermediate resize used in live mode. Single SetWindowPos,
    /// no FRAMECHANGED kick - the kick happens on the final commit.
    /// </summary>
    private void ScheduleLiveResize()
    {
        var target = _target;
        var rect = ComputeTargetRect();
        if (!rect.HasValue) return;
        var rectValue = rect.Value;
        var keepOnScreen = _settings.KeepWindowOnScreen;
        _dispatcher.BeginInvoke(() =>
        {
            if (!Native.IsWindow(target)) return;
            WindowSnapper.SnapToVisibleRect(target, rectValue, sendFrameChangedKick: false, keepOnScreen: keepOnScreen);
        });
    }

    /// <summary>
    /// Final commit on LBUTTONUP. Always one bounds-changing SetWindowPos
    /// (mirrors what a manual resize commits at the end of its modal
    /// sizing loop). In live mode it's followed by a SWP_FRAMECHANGED
    /// kick to settle apps that override WM_NCCALCSIZE (Groupy 2's tab
    /// host) after the streaming intermediate resizes; in snap-on-release
    /// mode the kick is omitted so fragile renderers see a single,
    /// clean state change.
    /// </summary>
    private void CommitFinalSnap(int x, int y)
    {
        // Update end-of-selection from the cursor at the moment of release.
        // For grid mode this re-resolves the cell; for free mode it
        // clamps and stores the final pixel coords.
        if (_freeMode)
        {
            HandleFreeMove(x, y);
        }
        else
        {
            var overlay = _activeOverlay;
            if (overlay is not null)
            {
                var endCell = overlay.CellFromPhysical(x, y) ?? _endCell;
                _endCell = endCell;
                overlay.SetSelection(_startCell, _endCell);
            }
        }

        var target = _target;
        var live = _settings.LiveResize;
        var keepOnScreen = _settings.KeepWindowOnScreen;
        var rect = ComputeTargetRect();
        _dispatcher.BeginInvoke(() =>
        {
            if (rect.HasValue && Native.IsWindow(target))
                WindowSnapper.SnapToVisibleRect(target, rect.Value, sendFrameChangedKick: live, keepOnScreen: keepOnScreen);
            ResetToIdle();
        });
    }

    private void ResetToIdle()
    {
        _overlays.Hide();
        _activeOverlay = null;
        _target = IntPtr.Zero;
        _state = State.Idle;
        _movePending = false;
        _lastResizedCell = null;
        _freeMode = false;
    }

    private static void CancelDragOperation()
    {
        // Order matters: ESC first (handles standard modal loops cleanly,
        // unconditional cancel), then synthetic LBUTTONUP (catches custom
        // drag implementations that don't have an ESC handler). If ESC
        // already killed the drag, the LBUTTONUP arrives at an idle
        // window proc with no prior LBUTTONDOWN visible to it (we swallow
        // those during the gesture) and is harmlessly ignored.
        var inputs = new Native.INPUT[3];
        inputs[0] = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.INPUT_UNION
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = Native.VK_ESCAPE,
                    dwFlags = 0,
                    dwExtraInfo = Native.WINGRID11_INJECTED_MAGIC,
                },
            },
        };
        inputs[1] = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.INPUT_UNION
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = Native.VK_ESCAPE,
                    dwFlags = Native.KEYEVENTF_KEYUP,
                    dwExtraInfo = Native.WINGRID11_INJECTED_MAGIC,
                },
            },
        };
        inputs[2] = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            u = new Native.INPUT_UNION
            {
                mi = new Native.MOUSEINPUT
                {
                    dwFlags = Native.MOUSEEVENTF_LEFTUP,
                    dwExtraInfo = Native.WINGRID11_INJECTED_MAGIC,
                },
            },
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>
    /// Force-reset the gesture state machine. Wired to the panic hotkey
    /// so the user can always escape a wedged gesture.
    /// </summary>
    public void Reset() => _dispatcher.BeginInvoke(ResetToIdle);

    public void Dispose()
    {
        _mouse.MouseDown -= OnMouseDown;
        _mouse.MouseUp -= OnMouseUp;
        _mouse.MouseMove -= OnMouseMove;
    }
}
