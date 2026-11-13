using SharpDX.XInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.Utilities.DeviceManagement.PnP;
using System.Reflection;

namespace CouchControl;

/// <summary>
/// Bridges a physical XInput controller to a ViGEm virtual Xbox 360 controller.
/// Mute state is controlled by JoyXoff virtual keyboard window detection and
/// configurable process foreground detection.
/// </summary>
public sealed class ControllerBridge : IDisposable
{
    private Controller? _xinput;
    private int _virtualSlot = -1;

    private ViGEmClient? _vigem;
    private IXbox360Controller? _virtual;
    private MethodInfo? _submitReport;

    private WindowWatcher? _windowWatcher;
    private DualSenseBridge? _dualSense;
    private DesktopMapper? _desktop;
    private readonly object _desktopLock = new();
    private DesktopModeSettings _desktopSettings = new();
    private bool _warnedJoyXoffRunning;
    private System.Threading.Timer? _hidHideTimer;
    private volatile bool _manageHidHide;

    /// <summary>Mute games while the Start menu, Search, tray or Explorer is in front.</summary>
    public volatile bool MuteOnWindowsUi = true;

    public ControllerBridge(bool manageHidHide, DesktopModeSettings desktopSettings)
    {
        _manageHidHide = manageHidHide;
        _desktopSettings = desktopSettings;
    }

    /// <summary>
    /// When on, physical controllers are hidden from games while CouchControl runs.
    /// Turning it off un-hides the ones CouchControl hid.
    /// </summary>
    public bool ManageHidHide
    {
        get => _manageHidHide;
        set
        {
            if (_manageHidHide == value) return;
            _manageHidHide = value;
            if (value)
            {
                HidHideManager.Initialize(true);
                HidHideManager.HidePhysicalControllers();
            }
            else
            {
                HidHideManager.ReleasePhysicalControllers();
            }
        }
    }

    private volatile bool _isMuted = false;
    private volatile bool _mutedByDesktop = false;    // built-in desktop mode active
    private volatile bool _mutedByProcess = false;    // Auto-mute process in foreground

    private void UpdateMuteState()
    {
        bool shouldMute = _mutedByDesktop || _mutedByProcess;
        if (shouldMute != _isMuted)
        {
            _isMuted = shouldMute;
            AppLogger.Log($"Mute state: {(shouldMute ? "MUTED" : "UNMUTED")} " +
                $"(desktop={_mutedByDesktop}, process={_mutedByProcess})");
        }
    }

    // ── Desktop mode ──────────────────────────────────────────────────────

    /// <summary>Raised (on the poll thread or caller's thread) when desktop mode turns on/off.</summary>
    public event Action<bool>? DesktopModeChanged;

    public bool DesktopModeActive => _desktop?.IsActive == true;

    public void SetDesktopMode(bool active)
    {
        lock (_desktopLock) _desktop?.SetActive(active);
    }

    public void ApplyDesktopSettings(DesktopModeSettings settings)
    {
        lock (_desktopLock)
        {
            _desktopSettings = settings;
            _desktop?.ApplySettings(settings);
        }
    }

    private void OnDesktopModeChanged(bool active)
    {
        _mutedByDesktop = active;
        UpdateMuteState();
        AppLogger.Log($"Desktop mode {(active ? "ON" : "OFF")}.");

        if (active && !_warnedJoyXoffRunning && System.Diagnostics.Process.GetProcessesByName("Joyxoff").Length > 0)
        {
            _warnedJoyXoffRunning = true;
            AppLogger.Log("WARNING: JoyXoff is still running and will also act on controller input. " +
                "Quit JoyXoff and turn off its 'Run on Windows startup' setting.");
        }
        DesktopModeChanged?.Invoke(active);
    }

    internal static PadState ToPadState(Gamepad gp)
    {
        var b = gp.Buttons;
        ushort digital = 0;
        void Set(GamepadButtonFlags flag, PadButton pad) { if (b.HasFlag(flag)) digital |= (ushort)(1 << (int)pad); }
        Set(GamepadButtonFlags.A, PadButton.A);
        Set(GamepadButtonFlags.B, PadButton.B);
        Set(GamepadButtonFlags.X, PadButton.X);
        Set(GamepadButtonFlags.Y, PadButton.Y);
        Set(GamepadButtonFlags.LeftShoulder, PadButton.LB);
        Set(GamepadButtonFlags.RightShoulder, PadButton.RB);
        Set(GamepadButtonFlags.Back, PadButton.Back);
        Set(GamepadButtonFlags.Start, PadButton.Start);
        Set(GamepadButtonFlags.LeftThumb, PadButton.LS);
        Set(GamepadButtonFlags.RightThumb, PadButton.RS);
        Set(GamepadButtonFlags.DPadUp, PadButton.DPadUp);
        Set(GamepadButtonFlags.DPadDown, PadButton.DPadDown);
        Set(GamepadButtonFlags.DPadLeft, PadButton.DPadLeft);
        Set(GamepadButtonFlags.DPadRight, PadButton.DPadRight);
        return new PadState(gp.LeftThumbX, gp.LeftThumbY, gp.RightThumbX, gp.RightThumbY,
            gp.LeftTrigger, gp.RightTrigger, digital);
    }

    internal const int DeadzoneRaw = 3276;

    public bool IsMuted => _isMuted;
    public int VirtualSlot => _virtualSlot;
    public int PhysicalSlot => _xinput != null ? (int)_xinput.UserIndex : -1;
    public bool DualSenseConnected => _dualSense?.IsConnected == true;

    /// <summary>Raised (on a worker thread) when a DualSense touchpad gesture is recognised.</summary>
    public event Action<TouchpadGesture>? TouchpadGestureDetected;
    public int DualSenseFeedSlot => _dualSense?.FeedSlot ?? -1;
    public bool DualSenseFeedHidden => _dualSense?.FeedHidden == true;

    public bool Initialize()
    {
        try
        {
            // HidHide first: clear entries a previous run left behind (ViGEm instance IDs can
            // be reused), fix the allow list, and hide physical pads before games can grab them.
            HidHideManager.Initialize(_manageHidHide);
            if (_manageHidHide)
                HidHideManager.HidePhysicalControllers();

            // Connect virtual controller next.
            // If CouchControl starts before physical controllers connect (e.g. at Windows startup
            // via "Start with Windows"), the virtual controller claims slot 0 automatically.
            _vigem = new ViGEmClient();
            _virtual = _vigem.CreateXbox360Controller();
            _submitReport = _virtual.GetType().GetMethod("SubmitReport",
                BindingFlags.Public | BindingFlags.Instance);
            if (_submitReport == null)
                AppLogger.Log("WARNING: SubmitReport not found.");
            _virtual.FeedbackReceived += OnGameRumble;
            _virtual.Connect();
            Thread.Sleep(300);

            // Identify virtual vs physical slots.
            // ViGEm virtual controllers have no XInput instance ID; physical controllers do.
            var allSlots = new List<(int slot, Controller ctrl)>();
            for (int i = 0; i < 4; i++)
            {
                var c = new Controller((UserIndex)i);
                if (c.IsConnected) allSlots.Add((i, c));
            }

            var physicalControllers = new List<Controller>();
            foreach (var (slot, ctrl) in allSlots)
            {
                var id = FindXInputInstanceId(slot);
                if (id == null)
                {
                    _virtualSlot = slot;
                    AppLogger.Log($"Virtual controller on slot {slot}.");
                }
                else
                {
                    physicalControllers.Add(ctrl);
                    AppLogger.Log($"Physical controller on slot {slot}.");
                }
            }

            if (_virtualSlot == -1 && allSlots.Count >= 1)
            {
                _virtualSlot = allSlots[0].slot;
                AppLogger.Log($"Virtual controller on slot {_virtualSlot} (fallback).");
            }

            _xinput = physicalControllers.Count > 0 ? physicalControllers[0] : FindPhysicalController();
            if (_xinput == null)
                AppLogger.Log("No physical controller found. Will retry during run loop.");

            if (_virtualSlot == 0)
                AppLogger.Log("Virtual controller on slot 0 — all games will see it correctly.");
            else
                AppLogger.Log($"Virtual controller on slot {_virtualSlot}. Enable 'Start with Windows' in the tray menu for automatic slot 0.");

            // Window watcher — auto-mutes when a configured process is in the foreground
            _windowWatcher = new WindowWatcher(
                onProcessChanged: processForeground =>
                {
                    _mutedByProcess = processForeground;
                    UpdateMuteState();
                },
                getAutoMuteProcesses: () => AppConfig.Load().AutoMuteProcesses,
                muteOnWindowsUi: () => MuteOnWindowsUi);
            _windowWatcher.Start();

            // Built-in desktop mode (mouse/keyboard from the controller). Games are muted
            // while it's on.
            _desktop = new DesktopMapper(InputSender.Instance, RunOneShot, _desktopSettings);
            _desktop.ModeChanged += OnDesktopModeChanged;
            AppLogger.Log($"Desktop mode ready (toggle: {_desktopSettings.ToggleChord}).");

            // DualSense → Xbox 360 feed controller. The run loop picks the feed up as a
            // "physical" XInput pad and mirrors it (with muting) to games.
            _dualSense = new DualSenseBridge(_vigem, g => TouchpadGestureDetected?.Invoke(g));
            _dualSense.Start();
            AppLogger.Log("DualSense watcher started.");

            // Catch controllers that connect later (e.g. when a stream starts).
            _hidHideTimer = new System.Threading.Timer(_ =>
            {
                if (_manageHidHide) HidHideManager.HidePhysicalControllers();
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            return true;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            AppLogger.Log($"Init error: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    private static string? FindXInputInstanceId(int slot)
    {
        try
        {
            int index = 0;
            while (Devcon.FindByInterfaceGuid(DeviceInterfaceIds.XUsbDevice,
                out string instancePath, out string instanceId, index++, true))
            {
                string slotHex = $"IG_{slot:X2}";
                if (instancePath.Contains(slotHex, StringComparison.OrdinalIgnoreCase))
                    return instanceId;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Passes rumble from games (sent to the main virtual pad) to the physical pad. For a
    /// DualSense, the physical pad is its feed controller, and DualSenseBridge relays the
    /// feed's rumble to the DualSense itself.
    /// </summary>
    private void OnGameRumble(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        SetPhysicalRumble(_xinput, e.LargeMotor, e.SmallMotor);
    }

    private static void SetPhysicalRumble(Controller? pad, byte large, byte small)
    {
        if (pad == null) return;
        try
        {
            pad.SetVibration(new Vibration
            {
                LeftMotorSpeed = (ushort)(large * 257),  // low-frequency (strong) motor
                RightMotorSpeed = (ushort)(small * 257), // high-frequency (weak) motor
            });
        }
        catch { } // disconnected mid-call; the run loop handles reconnects
    }

    private Controller? FindPhysicalController()
    {
        for (int i = 0; i < 4; i++)
        {
            if (i == _virtualSlot) continue;
            var ctrl = new Controller((UserIndex)i);
            if (ctrl.IsConnected) return ctrl;
        }
        return null;
    }

    private void RunOneShot(string name)
    {
        // Off the poll thread: these can take a moment (e.g. starting TabTip).
        Task.Run(() =>
        {
            switch (name)
            {
                case "TouchKeyboard": TouchKeyboard.Toggle(); break;
                case "ShowDesktop": GuideButtonWatcher.ShowDesktop(); break;
            }
        });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var pollClock = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_xinput == null || !_xinput.IsConnected)
                {
                    if (_xinput != null)
                    {
                        AppLogger.Log("Controller disconnected. Waiting for reconnect...");
                        _xinput = null;
                        lock (_desktopLock) _desktop?.Reset();
                    }
                    _xinput = FindPhysicalController();
                    if (_xinput == null)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    AppLogger.Log($"Controller reconnected on slot {(int)_xinput.UserIndex}.");
                    // A newly connected controller always starts in controller (game) mode.
                    lock (_desktopLock) _desktop?.SetActive(false);
                }

                _xinput.GetState(out var state);

                double dt = Math.Clamp(pollClock.Elapsed.TotalSeconds, 0, 0.05);
                pollClock.Restart();
                lock (_desktopLock) _desktop?.Update(ToPadState(state.Gamepad), dt);

                ApplyState(_isMuted ? ControllerState.Neutral : BuildReport(state.Gamepad));
            }
            catch (SharpDX.SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x8007001E))
            {
                AppLogger.Log("Controller disconnected. Waiting for reconnect...");
                _xinput = null;
                lock (_desktopLock) _desktop?.Reset();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLogger.Log($"Poll error: {ex.Message}");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    internal static ControllerState BuildReport(Gamepad gp)
    {
        var b = gp.Buttons;
        return new ControllerState(
            LeftThumbX:    ApplyDeadzone(gp.LeftThumbX),
            LeftThumbY:    ApplyDeadzone(gp.LeftThumbY),
            RightThumbX:   ApplyDeadzone(gp.RightThumbX),
            RightThumbY:   ApplyDeadzone(gp.RightThumbY),
            LeftTrigger:   gp.LeftTrigger,
            RightTrigger:  gp.RightTrigger,
            A:             b.HasFlag(GamepadButtonFlags.A),
            B:             b.HasFlag(GamepadButtonFlags.B),
            X:             b.HasFlag(GamepadButtonFlags.X),
            Y:             b.HasFlag(GamepadButtonFlags.Y),
            LeftShoulder:  b.HasFlag(GamepadButtonFlags.LeftShoulder),
            RightShoulder: b.HasFlag(GamepadButtonFlags.RightShoulder),
            Back:          b.HasFlag(GamepadButtonFlags.Back),
            Start:         b.HasFlag(GamepadButtonFlags.Start),
            LeftThumb:     b.HasFlag(GamepadButtonFlags.LeftThumb),
            RightThumb:    b.HasFlag(GamepadButtonFlags.RightThumb),
            Guide:         false,
            DPadUp:        b.HasFlag(GamepadButtonFlags.DPadUp),
            DPadDown:      b.HasFlag(GamepadButtonFlags.DPadDown),
            DPadLeft:      b.HasFlag(GamepadButtonFlags.DPadLeft),
            DPadRight:     b.HasFlag(GamepadButtonFlags.DPadRight));
    }

    internal static short ApplyDeadzone(short value)
    {
        if (value > -DeadzoneRaw && value < DeadzoneRaw) return 0;
        return value;
    }

    private void ApplyState(ControllerState s)
    {
        if (_virtual == null) return;
        WriteState(_virtual, s);
        _submitReport?.Invoke(_virtual, null);
    }

    /// <summary>Copies a <see cref="ControllerState"/> onto a ViGEm Xbox 360 target.</summary>
    internal static void WriteState(IXbox360Controller target, ControllerState s)
    {
        target.SetAxisValue(Xbox360Axis.LeftThumbX,  s.LeftThumbX);
        target.SetAxisValue(Xbox360Axis.LeftThumbY,  s.LeftThumbY);
        target.SetAxisValue(Xbox360Axis.RightThumbX, s.RightThumbX);
        target.SetAxisValue(Xbox360Axis.RightThumbY, s.RightThumbY);
        target.SetSliderValue(Xbox360Slider.LeftTrigger,  s.LeftTrigger);
        target.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
        target.SetButtonState(Xbox360Button.A,             s.A);
        target.SetButtonState(Xbox360Button.B,             s.B);
        target.SetButtonState(Xbox360Button.X,             s.X);
        target.SetButtonState(Xbox360Button.Y,             s.Y);
        target.SetButtonState(Xbox360Button.LeftShoulder,  s.LeftShoulder);
        target.SetButtonState(Xbox360Button.RightShoulder, s.RightShoulder);
        target.SetButtonState(Xbox360Button.Back,          s.Back);
        target.SetButtonState(Xbox360Button.Start,         s.Start);
        target.SetButtonState(Xbox360Button.LeftThumb,     s.LeftThumb);
        target.SetButtonState(Xbox360Button.RightThumb,    s.RightThumb);
        target.SetButtonState(Xbox360Button.Guide,         s.Guide);
        target.SetButtonState(Xbox360Button.Up,            s.DPadUp);
        target.SetButtonState(Xbox360Button.Down,          s.DPadDown);
        target.SetButtonState(Xbox360Button.Left,          s.DPadLeft);
        target.SetButtonState(Xbox360Button.Right,         s.DPadRight);
    }

    internal static long Map(long value, long inMin, long inMax, long outMin, long outMax)
        => (value - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    public void Dispose()
    {
        _hidHideTimer?.Dispose();
        _windowWatcher?.Stop();
        lock (_desktopLock) _desktop?.Reset(); // release any held keys/buttons
        SetPhysicalRumble(_xinput, 0, 0);
        _dualSense?.Dispose();
        HidHideManager.ReleaseAll(); // controllers behave normally once CouchControl exits
        _virtual?.Disconnect();
        _vigem?.Dispose();
        AppLogger.Log("Cleanup complete.");
    }
}

/// <summary>Plain DTO for virtual controller state.</summary>
internal record struct ControllerState(
    short LeftThumbX, short LeftThumbY,
    short RightThumbX, short RightThumbY,
    byte LeftTrigger, byte RightTrigger,
    bool A, bool B, bool X, bool Y,
    bool LeftShoulder, bool RightShoulder,
    bool Back, bool Start,
    bool LeftThumb, bool RightThumb,
    bool Guide,
    bool DPadUp, bool DPadDown, bool DPadLeft, bool DPadRight)
{
    public static ControllerState Neutral => default;
}
