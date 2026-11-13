using System.Text.Json;

namespace CouchControl;

/// <summary>
/// Persists CouchControl configuration to %AppData%\CouchControl\config.json.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Process names that trigger auto-mute when in the foreground. Replaces HidHide's
    /// block list: physical controllers are hidden from every app, so muting the virtual
    /// controller is what keeps these apps from reacting.
    /// </summary>
    public List<string> AutoMuteProcesses { get; set; } = new() { "Battle.net" };

    /// <summary>Mute games while the Start menu, Search, tray or Explorer is in front.</summary>
    public bool MuteOnWindowsUi { get; set; } = true;

    /// <summary>Whether CouchControl starts automatically with Windows.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Guide/PS button: tap toggles the touch keyboard, double-tap shows the desktop.</summary>
    public bool GuideButtonActions { get; set; } = true;

    /// <summary>DualSense touchpad: long touch toggles the touch keyboard, double-tap shows the desktop.</summary>
    public bool TouchpadGestures { get; set; } = true;

    /// <summary>
    /// Let CouchControl configure HidHide: allow-list itself and JoyXoff, and hide
    /// physical controllers from games while CouchControl is running.
    /// </summary>
    public bool ManageHidHide { get; set; } = true;

    /// <summary>Built-in desktop mode (mouse and keyboard from the controller).</summary>
    public DesktopModeSettings DesktopMode { get; set; } = new();

    public static AppConfig Load()
    {
        AppPaths.MigrateFromXMute();
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ConfigFile, json);
        }
        catch { }
    }
}
