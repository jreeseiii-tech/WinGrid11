# WinGrid11

Dynamic grid-based window snapping for Windows 11. Spiritual successor to [WindowGrid](http://windowgrid.net).

> Heavily inspired by the great work of Joshua Wilding. The original WindowGrid was an amazing tool that changed how I use Windows. Sadly, it is pretty inconsistent on Windows 11 and with newer apps. This tool aims to address that. Check out the original WindowGrid at [windowgrid.net](http://windowgrid.net).

This is a (largely) vibe coded app based on the old WindowGrid functions that don't consitently work these days.

## What does it do??

You hold left-click on a window's title bar like you're about to drag it. While still holding, you tap right-click. A grid pops up over every monitor. You drag from one corner of the rectangle you want to the other corner. You release left-click. The window snaps to that rectangle.

That's the whole thing. Point at the size and shape you want, let go, done.

Via this resizing method, you can also use a free resizing mode that lets you dynamically resize the window to any dimmension you want - the grid is not required!

## What's different from the original

The original WindowGrid is a brilliant tool that quietly stopped getting updates around 2016. On modern Windows 11 it has a few problems that compound the more you use it:

- **It may distort or break on modern apps.** Electron - and Chromium-based apps like Cursor, VS Code, Discord, Slack, Obsidian, and custom-renderer apps like FilePilot may get distorted with every resize (or have weird issues like black render areas). Content may shift inside of the window, gaps appear at the edges, etc etc. The tool was designed for an era of WPF and Win32 apps that have since become a minority.
- **It didn't handle DPI scaling well.** Pair a 4K monitor with a 1080p one and snaps land a few (or a lot of) pixels off on whichever monitor isn't your "main" one. Or, even just using a differnt Windows sclaing, or a custome one, mwould cause the grid to become innaccurate.
- **It doesn't play with Stardock Groupy 2.** Groupy wraps tabbed windows in its own host and the original snapper targets the inner tab, which leaves Groupy out of sync. I know, I am the only one using this tool, but I like it.
- **It uses a DLL-injection model** that today gets flagged by AV/EDR, breaks across integrity levels, and stumbles on packaged apps.

WinGrid11 fixes all of these. The biggest practical change is the resize model: instead of live-resizing the window every time the cursor crosses a grid line (which is what kicks Electron/Chromium apps into their corrupt-redraw paths), the original window stays put while the overlay shows a preview, and a single clean resize is applied when you release. The old live-resize behaviour is still available as a toggle if you prefer it for traditional apps where it works fine.

The other big change is that the whole tool is one process with no DLL injection, and a self-contained installer that doesn't need .NET on the user's machine. Drag detection happens through `SetWinEventHook` running out-of-process, and every HWND it sees is resolved to its true root window so apps (like Groupy2) aren't broken on resize. All grid math runs in physical pixels of the monitor under the cursor, with DWM extended-frame compensation so the visible edges of the window line up with the cells you picked.

## Gesture

1. Start dragging a window by its title bar (left mouse button).
2. While still holding LMB, press and hold the right mouse button. The grid appears on every monitor.
3. Hover the cell that should be the **start corner** of the snapped window. Release RMB to anchor it.
4. Move the cursor to the **end corner**. The window resizes on release (or live, if that's toggled).
5. Release LMB. The window stays snapped.

Releasing LMB while still holding RMB cancels the gesture. The panic hotkey **Ctrl+Alt+Shift+Q** force-resets the state machine if anything ever feels stuck.

## Architecture

- **Single process. No DLL injection.** Drag detection via `SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART/END, ..., WINEVENT_OUTOFCONTEXT)` - out-of-process, AV-friendly, Groupy-friendly.
- **`WH_MOUSE_LL`** for the RMB trigger; runs only in our process. The panic hotkey (Ctrl+Alt+Shift+Q) uses `RegisterHotKey` instead, so the process never sees other keystrokes.
- **Per-Monitor v2 DPI awareness** baked into the manifest. All grid math is done in physical pixels of the monitor under the cursor; each overlay is positioned via `SetWindowPos` in physical coordinates.
- **DWM extended frame compensation** via `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` so the *visible* window edges align with the chosen cells, not the invisible resize-border rect.
- **Snap on release** by default. The system's modal move loop is exited via a synthesised `SendInput(ESC + LBUTTONUP)` and a single `SetWindowPos` is applied on the user's real `WM_LBUTTONUP`. Optional live-resize mode applies the position on every move with `SWP_FRAMECHANGED` for traditional apps.
- **Groupy 2 compatibility**: every HWND from the WinEvent watcher is resolved via `GetAncestor(GA_ROOT)` before being snapped, so the movable tab-host window is the thing that actually moves.
- **UIPI awareness**: a "Restart as administrator" tray option opts in to managing elevated apps (Task Manager, regedit, etc.) when needed.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build .\WinGrid11.sln -c Release
dotnet run --project .\src\WinGrid11\WinGrid11.csproj -c Release
```

To produce a versioned, self-contained installer at `.\dist\WinGrid11-<version>-Setup.exe`:

```powershell
pwsh .\build-installer.ps1
```

The script prompts for a version (with patch/minor/major shortcuts), persists it back to `WinGrid11.csproj`, then runs `dotnet publish` and Inno Setup. The installer is built with [Inno Setup 6](https://jrsoftware.org/isdl.php) by Jordan Russell.

## Settings

Settings live at `%AppData%\WinGrid11\settings.json` and are editable through the tray menu or the dedicated settings window:

- Grid dimensions (columns × rows)
- Free resize on/off, plus "right-click again switches to free"
- Resize window during gesture (live vs snap-on-release)
- Keep windows on screen when min-size pushes them off
- Block cursor interaction with background apps during gesture
- Cell, highlight, and stroke colours
- Launch on Windows startup

## License

MIT. See [LICENSE](LICENSE). Provided as-is, no warranty. WinGrid11 installs a low-level mouse hook and manipulates other apps' windows via Win32 APIs - that's how the gesture works and it's all the tool does, but you should be comfortable with that trade-off before installing.
