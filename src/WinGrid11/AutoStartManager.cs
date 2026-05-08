using Microsoft.Win32;

namespace WinGrid11;

/// <summary>
/// Manages WinGrid11's per-user "launch at Windows startup" entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// HKCU is the canonical store for per-user autostart on Windows: it
/// works without elevation, survives app updates (Inno Setup keeps the
/// AppId stable so the install path doesn't change), and is what the
/// Task Manager Startup tab displays. The installer can pre-populate
/// this value when the user ticks the autostart task; the in-app
/// settings checkbox reads/writes the same key so toggling either
/// place stays in sync.
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinGrid11";

    /// <summary>
    /// True if the autostart entry exists and is non-empty. We don't
    /// validate that the path it points to actually exists - Inno
    /// Setup's uninstaller scrubs the value on uninstall, and the user
    /// is the only other writer.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Enable autostart (set the registry value to the current process's
    /// quoted exe path) or disable it (delete the value). Quoting the
    /// path matters when the install dir contains spaces, e.g. Program
    /// Files.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return false;
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}
