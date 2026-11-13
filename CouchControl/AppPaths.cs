namespace CouchControl;

/// <summary>
/// Central location for CouchControl's data files (%AppData%\CouchControl).
/// On first run after the rename from X-Mute, settings are copied over from
/// %AppData%\X-Mute, and X-Mute's leftover HidHide feed entry is cleaned up.
/// </summary>
internal static class AppPaths
{
    public const string AppName = "CouchControl";

    private static readonly string AppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static readonly string DataDir = Path.Combine(AppData, AppName);
    public static readonly string LegacyDataDir = Path.Combine(AppData, "X-Mute");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogFile => Path.Combine(DataDir, "couchcontrol.log");
    public static string HidHideStateFile => Path.Combine(DataDir, "hidhide-owned.txt");
    public static string HidHideBackupFile => Path.Combine(DataDir, "hidhide-backup.json");
    public static string LegacyHidHideFeedFile => Path.Combine(LegacyDataDir, "hidhide-feed.txt");

    private static bool _migrated;

    /// <summary>Copies config.json from the old X-Mute folder once, if CouchControl has none yet.</summary>
    public static void MigrateFromXMute()
    {
        if (_migrated) return;
        _migrated = true;
        try
        {
            Directory.CreateDirectory(DataDir);
            var legacyConfig = Path.Combine(LegacyDataDir, "config.json");
            if (!File.Exists(ConfigFile) && File.Exists(legacyConfig))
                File.Copy(legacyConfig, ConfigFile);
        }
        catch { }
    }
}
