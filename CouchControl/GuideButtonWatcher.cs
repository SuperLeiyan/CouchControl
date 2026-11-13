using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchControl;

/// <summary>
/// Ported from GuideTray. Watches every XInput slot for the Guide button (Xbox button,
/// or the DualSense PS button via the DualSense feed controller):
///   single tap  → toggle the Windows touch keyboard
///   double tap  → show desktop
///   long press  → ignored (≥ 1 s, e.g. holding to power the controller off)
///
/// CouchControl is on HidHide's allow list, so it sees hidden physical pads and the
/// hidden DualSense feed that a separate app like GuideTray could not.
/// </summary>
public sealed class GuideButtonWatcher : IDisposable
{
    private const ushort GuideButton = 0x0400;
    private const int LongPressThresholdMs = 1000;
    private const int DoubleTapWindowMs = 400;
    private const int PollIntervalMs = 50;

    private readonly Func<bool> _isEnabled;
    private Thread? _thread;
    private volatile bool _running;
    private bool _loggedGuideTrayConflict;

    public GuideButtonWatcher(Func<bool> isEnabled) => _isEnabled = isEnabled;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = "GuideButtonWatcher" };
        _thread.Start();
        AppLogger.Log("Guide button watcher started.");
    }

    private void Poll()
    {
        bool wasPressed = false;
        DateTime pressedAt = DateTime.MinValue;
        DateTime lastReleaseAt = DateTime.MinValue;
        int tapCount = 0;

        while (_running)
        {
            try
            {
                bool guideDown = _isEnabled() && IsGuideDownOnAnySlot();
                var now = DateTime.UtcNow;

                if (guideDown && !wasPressed)
                    pressedAt = now;

                if (!guideDown && wasPressed && (now - pressedAt).TotalMilliseconds < LongPressThresholdMs)
                {
                    if ((now - lastReleaseAt).TotalMilliseconds <= DoubleTapWindowMs)
                    {
                        tapCount = 0;
                        lastReleaseAt = DateTime.MinValue;
                        Fire("double tap → show desktop", ShowDesktop);
                    }
                    else
                    {
                        tapCount = 1;
                        lastReleaseAt = now;
                    }
                }

                // Single tap confirmed once the double-tap window has expired.
                if (tapCount == 1 && (now - lastReleaseAt).TotalMilliseconds > DoubleTapWindowMs)
                {
                    tapCount = 0;
                    Fire("tap → touch keyboard", TouchKeyboard.Toggle);
                }

                wasPressed = guideDown;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Guide watcher error: {ex.Message}");
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    private void Fire(string description, Action action)
    {
        // If the standalone GuideTray is still running, both apps would toggle the
        // keyboard on the same press and cancel each other out.
        if (Process.GetProcessesByName("GuideTray").Length > 0)
        {
            if (!_loggedGuideTrayConflict)
            {
                AppLogger.Log("Guide: GuideTray.exe is running — skipping CouchControl's guide actions. Quit GuideTray to use CouchControl's.");
                _loggedGuideTrayConflict = true;
            }
            return;
        }
        _loggedGuideTrayConflict = false;

        AppLogger.Log($"Guide: {description}");
        action();
    }

    private static bool IsGuideDownOnAnySlot()
    {
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetStateEx(i, out var state) == 0 && (state.Gamepad.wButtons & GuideButton) != 0)
                return true;
        }
        return false;
    }

    // ── Show desktop ──────────────────────────────────────────────────────

    internal static void ShowDesktop()
    {
        // Minimize the foreground window first (handles fullscreen games),
        // then the shell's Show Desktop (same as Win+D).
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            PostMessage(fg, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
            ShowWindow(fg, SW_MINIMIZE);
        }

        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            shell?.MinimizeAll();
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Show desktop failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }

    // ── Interop ───────────────────────────────────────────────────────────

    private const int SW_MINIMIZE = 6;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    // Ordinal 100 = XInputGetStateEx, which (unlike XInputGetState) reports the Guide button.
    // xinput1_4 ships with Windows 10/11, so no DirectX redistributable is needed.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint dwUserIndex, out XINPUT_STATE pState);
}

/// <summary>Toggles the Windows touch keyboard via TabTip's ITipInvocation COM interface.</summary>
internal static class TouchKeyboard
{
    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UIHostNoLaunch { }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation { void Toggle(IntPtr hwnd); }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int w, int h,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

    public static void Toggle()
    {
        IntPtr helperHwnd = IntPtr.Zero;
        try
        {
            EnsureTabTipRunning();

            helperHwnd = CreateWindowEx(
                0x08000000 /* WS_EX_NOACTIVATE */, "STATIC", null, 0x80000000 /* WS_POPUP */,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            var uiHost = new UIHostNoLaunch();
            ((ITipInvocation)uiHost).Toggle(helperHwnd != IntPtr.Zero ? helperHwnd : GetDesktopWindow());
            Marshal.ReleaseComObject(uiHost);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Touch keyboard toggle failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            if (helperHwnd != IntPtr.Zero) DestroyWindow(helperHwnd);
        }
    }

    private static void EnsureTabTipRunning()
    {
        if (Process.GetProcessesByName("TabTip").Length > 0) return;

        string tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "TabTip.exe");
        if (!File.Exists(tabTip)) return;

        Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
        Thread.Sleep(500); // let it register its COM objects
    }
}
