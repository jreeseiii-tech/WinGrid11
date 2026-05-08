using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using System.Windows;
using System.Windows.Forms;
using WinGrid11.Gesture;
using WinGrid11.Input;
using WinGrid11.Overlay;
using WinGrid11.Ui;
using WinGrid11.Win32;
using WinGrid11.WindowOps;
using Application = System.Windows.Application;

namespace WinGrid11;

public partial class App : Application
{
    private Settings? _settings;
    private LowLevelMouseHook? _mouseHook;
    private HotKeyManager? _panicHotKey;
    private DragWatcher? _dragWatcher;
    private OverlayManager? _overlayManager;
    private GestureEngine? _engine;
    private NotifyIcon? _tray;
    private SingleInstanceCoordinator? _singleInstance;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // First gate: only one WinGrid11 per session. A second launch
        // (Start Menu shortcut while autostart already brought us up,
        // installer post-install launch, etc.) pings the running
        // instance to surface its settings window, then exits here
        // before any hooks or tray icons are created.
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstanceCoordinator.NudgeExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _settings = Settings.Load();

        _mouseHook = new LowLevelMouseHook();
        _dragWatcher = new DragWatcher();
        _overlayManager = new OverlayManager(_settings);
        _overlayManager.Initialize();
        _engine = new GestureEngine(_mouseHook, _dragWatcher, _overlayManager, _settings, Dispatcher);

        // Panic hotkey: Ctrl+Alt+Shift+Q force-resets the gesture state
        // machine. Registered through the OS hotkey API so we never see
        // any other keystroke - keeps the keylogger-shaped capability of
        // a low-level keyboard hook out of the process entirely.
        _panicHotKey = new HotKeyManager(Native.VK_Q, ctrl: true, alt: true, shift: true);
        _panicHotKey.Pressed += () => _engine?.Reset();

        _mouseHook.Install();
        _dragWatcher.Start();

        // Listen for "second-instance launched" pings from the named
        // pipe and pop the settings window in response. Marshal off
        // the worker thread that delivers the event.
        _singleInstance.ActivationRequested += () => Dispatcher.BeginInvoke(ShowSettingsWindow);
        _singleInstance.StartServer();

        var elevated = IsRunningElevated();
        _tray = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = $"WinGrid11 - {_settings.Columns}×{_settings.Rows} grid"
                   + (elevated ? " (admin)" : ""),
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(elevated),
        };
        _tray.DoubleClick += (_, _) => ShowSettingsWindow();
    }

    /// <summary>
    /// True if our process token has the BUILTIN\Administrators group
    /// active. UIPI gates low-level hook delivery and SetWindowPos calls
    /// based on integrity level, so this directly determines whether we
    /// can manage Task Manager / regedit / other elevated apps.
    /// </summary>
    private static bool IsRunningElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Relaunches WinGrid11 with the "runas" verb (UAC prompt) and exits
    /// the current instance once the elevated copy has started. Used by
    /// the "Restart as administrator" tray item to opt into managing
    /// elevated windows (Task Manager, regedit, processes started under
    /// "Run as administrator", etc.) without requiring elevation on every
    /// launch.
    /// </summary>
    private void RestartElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return;

        // Hand the single-instance lock off to the elevated child.
        // Without this the child would race against our own mutex and
        // mistake its parent for a competing process.
        bool releasedForHandoff = _singleInstance?.ReleaseForHandoff() ?? false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
            });
        }
        catch
        {
            // User declined the UAC prompt, or shell launch failed.
            // Stay running as current user - reclaim the mutex so
            // future second-launch attempts still bounce off us.
            if (releasedForHandoff) _singleInstance?.ReacquireAfterFailedHandoff();
            return;
        }

        Shutdown();
    }

    // Checkable quick-toggle items hoisted onto fields so the menu's
    // Opening handler can refresh their Checked state from the live
    // Settings object without rebuilding the menu.
    private ToolStripMenuItem? _freeResizeItem;
    private ToolStripMenuItem? _rmbAgainItem;
    private ToolStripMenuItem? _keepOnScreenItem;

    private ContextMenuStrip BuildTrayMenu(bool elevated)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(elevated ? "WinGrid11 (running as administrator)" : "WinGrid11").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        _freeResizeItem = new ToolStripMenuItem("Free resize") { CheckOnClick = true };
        _freeResizeItem.Click += (_, _) =>
        {
            if (_settings is null) return;
            _settings.FreeResize = _freeResizeItem.Checked;
            PersistAndSync();
        };

        _rmbAgainItem = new ToolStripMenuItem("Right-click again switches to free") { CheckOnClick = true };
        _rmbAgainItem.Click += (_, _) =>
        {
            if (_settings is null) return;
            _settings.RmbAgainSwitchesToFree = _rmbAgainItem.Checked;
            PersistAndSync();
        };

        _keepOnScreenItem = new ToolStripMenuItem("Keep windows on screen") { CheckOnClick = true };
        _keepOnScreenItem.Click += (_, _) =>
        {
            if (_settings is null) return;
            _settings.KeepWindowOnScreen = _keepOnScreenItem.Checked;
            PersistAndSync();
        };

        menu.Items.Add(_freeResizeItem);
        menu.Items.Add(_rmbAgainItem);
        menu.Items.Add(_keepOnScreenItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Settings…", null, (_, _) => ShowSettingsWindow());
        menu.Items.Add("Reset gesture (Ctrl+Alt+Shift+Q)", null, (_, _) => _engine?.Reset());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        // Refresh check states from settings each time the menu opens
        // so toggles made elsewhere (settings window) are reflected.
        menu.Opening += (_, _) => SyncTrayCheckedState();
        return menu;
    }

    /// <summary>
    /// Mirror the live Settings object onto the tray menu's checkable
    /// items. RMB-again is disabled while Free resize is active because
    /// it only applies inside grid mode - same logic as the settings
    /// window keeps the two surfaces consistent.
    /// </summary>
    private void SyncTrayCheckedState()
    {
        if (_settings is null) return;
        if (_freeResizeItem is not null) _freeResizeItem.Checked = _settings.FreeResize;
        if (_keepOnScreenItem is not null) _keepOnScreenItem.Checked = _settings.KeepWindowOnScreen;
        if (_rmbAgainItem is not null)
        {
            _rmbAgainItem.Checked = _settings.RmbAgainSwitchesToFree;
            _rmbAgainItem.Enabled = !_settings.FreeResize;
            _rmbAgainItem.ToolTipText = _settings.FreeResize
                ? "Already in free resize mode."
                : "Click RMB again after the grid appears to switch to free resize.";
        }
    }

    private void PersistAndSync()
    {
        try { _settings?.Save(); } catch { /* I/O blip - in-memory state is still good */ }
        // If the settings window is open, push the new values into its
        // checkboxes so both surfaces agree. Cheap no-op if it's closed.
        _settingsWindow?.RefreshFromSettings();
    }

    private SettingsWindow? _settingsWindow;

    private void ShowSettingsWindow()
    {
        // Single instance - bring to front if already open. The window
        // mutates the live Settings object directly, so there's no
        // "apply" step to worry about across instances.
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        if (_settings is null || _overlayManager is null || _engine is null) return;

        var elevated = IsRunningElevated();
        _settingsWindow = new SettingsWindow(
            _settings,
            _overlayManager,
            _engine,
            elevated,
            RestartElevated,
            Shutdown);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        if (_tray is not null)
        {
            // NotifyIcon doesn't dispose the assigned Icon for us;
            // grab it before Dispose nulls it out.
            var icon = _tray.Icon;
            _tray.Dispose();
            icon?.Dispose();
        }
        _engine?.Dispose();
        _overlayManager?.Dispose();
        _dragWatcher?.Dispose();
        _panicHotKey?.Dispose();
        _mouseHook?.Dispose();
        _singleInstance?.Dispose();
    }
}
