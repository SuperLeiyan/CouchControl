using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchControl;

/// <summary>
/// Polls the foreground window and fires a callback when an auto-mute process
/// gains or loses the foreground.
/// </summary>
public sealed class WindowWatcher
{
    /// <summary>
    /// Windows shell surfaces that navigate with a controller: Start menu, Search,
    /// notification centre / quick settings / calendar, and Explorer (taskbar, tray
    /// overflow, desktop, File Explorer).
    /// </summary>
    internal static readonly string[] WindowsUiProcesses =
        ["StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "explorer"];

    private readonly Action<bool> _onProcessChanged;
    private readonly Func<IReadOnlyList<string>> _getAutoMuteProcesses;
    private readonly Func<bool> _muteOnWindowsUi;
    private Thread? _thread;
    private volatile bool _running;
    private bool _lastProcessState = false;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public WindowWatcher(Action<bool> onProcessChanged, Func<IReadOnlyList<string>> getAutoMuteProcesses,
        Func<bool> muteOnWindowsUi)
    {
        _onProcessChanged = onProcessChanged;
        _getAutoMuteProcesses = getAutoMuteProcesses;
        _muteOnWindowsUi = muteOnWindowsUi;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = "WindowWatcher" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
    }

    private void Poll()
    {
        while (_running)
        {
            bool processForeground = IsMuteProcessForeground();
            if (processForeground != _lastProcessState)
            {
                _lastProcessState = processForeground;
                _onProcessChanged(processForeground);
            }

            Thread.Sleep(100);
        }
    }

    private bool IsMuteProcessForeground()
    {
        var processes = _getAutoMuteProcesses();
        bool windowsUi = _muteOnWindowsUi();
        if (processes.Count == 0 && !windowsUi) return false;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            if (proc.Id == Environment.ProcessId) return false;
            return IsMuteProcess(proc.ProcessName, processes, windowsUi);
        }
        catch { return false; }
    }

    /// <summary>True if <paramref name="processName"/> should mute games (case-insensitive).</summary>
    internal static bool IsMuteProcess(string processName, IReadOnlyList<string> autoMuteProcesses, bool muteOnWindowsUi)
        => autoMuteProcesses.Any(p => processName.Equals(p, StringComparison.OrdinalIgnoreCase))
        || (muteOnWindowsUi && WindowsUiProcesses.Any(p => processName.Equals(p, StringComparison.OrdinalIgnoreCase)));
}
