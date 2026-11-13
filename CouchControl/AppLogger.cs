namespace CouchControl;

/// <summary>
/// Simple file logger. Writes to %AppData%\CouchControl\couchcontrol.log.
/// </summary>
internal static class AppLogger
{
    private static readonly object _lock = new();

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch { }
    }
}
