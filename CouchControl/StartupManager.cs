using Microsoft.Win32;

namespace CouchControl;

/// <summary>
/// Manages the Windows startup registry entry for CouchControl.
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = AppPaths.AppName;
    private const string LegacyAppName = "X-Mute";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        key?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
        key?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
    }

    public static void Toggle()
    {
        if (IsEnabled()) Disable(); else Enable();
    }
}
