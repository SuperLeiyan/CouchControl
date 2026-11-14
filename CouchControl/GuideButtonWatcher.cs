using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchControl;

/// <summary>
/// Ported from GuideTray. Watches every XInput slot for the Guide (Xbox) button, plus the
/// DualSense PS button reported by <see cref="DualSenseBridge"/>:
///   press       → toggle the Windows touch keyboard (fires on release)
///   long press  → ignored (≥ 1 s, e.g. holding to power the controller off)
///
/// CouchControl is on HidHide's allow list, so it sees hidden physical pads that a
/// separate app like GuideTray could not.
/// </summary>
public sealed class GuideButtonWatcher : IDisposable
{
    private const ushort GuideButton = 0x0400;
    private const int LongPressThresholdMs = 1000;
    private const int PollIntervalMs = 50;

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isDualSenseGuideDown;
    private readonly Func<int> _activeXInputSlot;
    private Thread? _thread;
    private volatile bool _running;
    private bool _loggedGuideTrayConflict;

    /// <summary>Only the controller in control can toggle the keyboard.</summary>
    /// <param name="isDualSenseGuideDown">PS button held on the DualSense in control.</param>
    /// <param name="activeXInputSlot">Slot of the Xbox controller in control, or -1.</param>
    public GuideButtonWatcher(Func<bool> isEnabled, Func<bool> isDualSenseGuideDown, Func<int> activeXInputSlot)
    {
        _isEnabled = isEnabled;
        _isDualSenseGuideDown = isDualSenseGuideDown;
        _activeXInputSlot = activeXInputSlot;
    }

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

        while (_running)
        {
            try
            {
                bool guideDown = _isEnabled() && (_isDualSenseGuideDown() || IsGuideDown(_activeXInputSlot()));
                var now = DateTime.UtcNow;

                if (guideDown && !wasPressed)
                    pressedAt = now;

                if (!guideDown && wasPressed && (now - pressedAt).TotalMilliseconds < LongPressThresholdMs)
                    Fire("press → keyboard", KeyboardHost.Toggle);

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

    private static bool IsGuideDown(int slot)
        => slot is >= 0 and < 4
           && XInputGetStateEx((uint)slot, out var state) == 0
           && (state.Gamepad.wButtons & GuideButton) != 0;

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
    // ── Visibility (IFrameworkInputPane reports the keyboard's on-screen rectangle) ──

    [ComImport, Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
    private class FrameworkInputPane { }

    [ComImport, Guid("5752238B-24F0-495A-82F1-2FD593056796")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameworkInputPane
    {
        void Advise([MarshalAs(UnmanagedType.IUnknown)] object pWindow, IntPtr pHandler, out int pdwCookie);
        void AdviseWithHWND(IntPtr hwnd, IntPtr pHandler, out int pdwCookie);
        void Unadvise(int dwCookie);
        void Location(out RECT prcInputPaneScreenLocation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static volatile bool _visible;
    private static Thread? _monitor;

    /// <summary>True while the touch keyboard is on screen (updated every 150 ms).</summary>
    public static bool IsVisible => _visible;

    /// <summary>Starts polling the keyboard's visibility on a background STA thread.</summary>
    public static void StartMonitor()
    {
        if (_monitor != null) return;
        _monitor = new Thread(() =>
        {
            IFrameworkInputPane? pane = null;
            bool loggedError = false;
            while (true)
            {
                try
                {
                    pane ??= (IFrameworkInputPane)new FrameworkInputPane();
                    pane.Location(out var r);
                    bool visible = r.Right > r.Left && r.Bottom > r.Top;
                    if (visible != _visible)
                    {
                        _visible = visible;
                        AppLogger.Log($"Touch keyboard {(visible ? "shown" : "hidden")}.");
                    }
                }
                catch (Exception ex)
                {
                    if (!loggedError)
                    {
                        loggedError = true;
                        AppLogger.Log($"Touch keyboard visibility check failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                    }
                    pane = null;
                    _visible = false;
                }
                Thread.Sleep(150);
            }
        }) { IsBackground = true, Name = "TouchKeyboardMonitor" };
        _monitor.SetApartmentState(ApartmentState.STA);
        _monitor.Start();
    }

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
        bool wasVisible = _visible;
        ToggleCore();
        // Diagnostics: report toggles that didn't change anything.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            if (_monitor != null && _visible == wasVisible)
                AppLogger.Log($"Touch keyboard toggle had no effect (still {(wasVisible ? "shown" : "hidden")}).");
        });
    }

    private static void ToggleCore()
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
