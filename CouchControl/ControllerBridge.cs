using SharpDX.XInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.Utilities.DeviceManagement.PnP;
using System.Reflection;

namespace CouchControl;

/// <summary>
/// Bridges a physical controller (an XInput pad, or a DualSense read directly over HID)
/// to a ViGEm virtual Xbox 360 controller, and passes game rumble back to it.
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

    internal static PadState ToPadState(ControllerState s)
    {
        ushort digital = 0;
        void Set(bool down, PadButton pad) { if (down) digital |= (ushort)(1 << (int)pad); }
        Set(s.A, PadButton.A);
        Set(s.B, PadButton.B);
        Set(s.X, PadButton.X);
        Set(s.Y, PadButton.Y);
        Set(s.LeftShoulder, PadButton.LB);
        Set(s.RightShoulder, PadButton.RB);
        Set(s.Back, PadButton.Back);
        Set(s.Start, PadButton.Start);
        Set(s.LeftThumb, PadButton.LS);
        Set(s.RightThumb, PadButton.RS);
        Set(s.DPadUp, PadButton.DPadUp);
        Set(s.DPadDown, PadButton.DPadDown);
        Set(s.DPadLeft, PadButton.DPadLeft);
        Set(s.DPadRight, PadButton.DPadRight);
        return new PadState(s.LeftThumbX, s.LeftThumbY, s.RightThumbX, s.RightThumbY,
            s.LeftTrigger, s.RightTrigger, digital);
    }

    internal const int DeadzoneRaw = 3276;

    /// <summary>Which physical controller is currently driving the virtual pad.</summary>
    internal enum InputSource { None, XInput, DualSense }
    private bool _keyboardNav; // poll thread only
    private volatile InputSource _source = InputSource.None;

    public bool IsMuted => _isMuted;
    public int VirtualSlot => _virtualSlot;
    public int PhysicalSlot => _source == InputSource.XInput && _xinput != null ? (int)_xinput.UserIndex : -1;
    public bool DualSenseConnected => _dualSense?.IsConnected == true;
    public bool DualSenseBluetooth => _dualSense?.IsBluetooth == true;
    /// <summary>True while the DualSense is the controller in control and its PS button is held.</summary>
    public bool DualSenseGuideDown => _source == InputSource.DualSense && _dualSense?.GuideDown == true;

    /// <summary>XInput slot of the Xbox controller in control, or -1 if it isn't an Xbox controller.</summary>
    public int ActiveXInputSlot => _source == InputSource.XInput && _xinput is { } x ? (int)x.UserIndex : -1;

    /// <summary>Raised (on a worker thread) when a DualSense touchpad gesture is recognised.</summary>
    public event Action<TouchpadGesture>? TouchpadGestureDetected;

    public bool Initialize()
    {
        try
        {
            // HidHide first: clear entries a previous run left behind (ViGEm instance IDs can
            // be reused), fix the allow list, and hide physical pads before games can grab them.
            HidHideManager.Initialize(_manageHidHide);
            if (_manageHidHide)
                HidHideManager.HidePhysicalControllers();

            // Connect the virtual controller next. XInput gives it the first free slot, so it
            // only becomes player 1 (slot 0) if no physical pad is on slot 0 yet. If one is,
            // the run loop moves it to slot 0 once that slot frees up (see ReclaimSlotZero).
            _vigem = new ViGEmClient();
            ConnectVirtual();

            _xinput = FindPhysicalController();
            AppLogger.Log(_xinput != null
                ? $"Physical controller on slot {(int)_xinput.UserIndex}."
                : "No physical controller found. Will retry during run loop.");

            if (_virtualSlot == 0)
                AppLogger.Log("Virtual controller on slot 0 — all games will see it correctly.");
            else
                AppLogger.Log($"Virtual controller on slot {_virtualSlot}: a controller was already on slot 0. " +
                    "Turn it off and on again and CouchControl will move to slot 0.");

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

            // DualSense reader. When a DualSense is connected, the run loop uses it in place
            // of an XInput pad and mirrors it (with muting) to games.
            _dualSense = new DualSenseBridge(g =>
            {
                // Only the controller in control can toggle the keyboard.
                if (_source == InputSource.DualSense) TouchpadGestureDetected?.Invoke(g);
            });
            _dualSense.Start();
            AppLogger.Log("DualSense watcher started.");

            TouchKeyboard.StartMonitor();

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

    /// <summary>Passes rumble from games (sent to the main virtual pad) to the physical controller.</summary>
    private void OnGameRumble(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        if (_source == InputSource.DualSense)
            _dualSense?.SetRumble(e.LargeMotor, e.SmallMotor);
        else
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

    /// <summary>Creates and connects the virtual controller and works out which slot it got.</summary>
    private void ConnectVirtual()
    {
        var before = ConnectedSlots();
        var pad = _vigem!.CreateXbox360Controller();
        _submitReport ??= pad.GetType().GetMethod("SubmitReport", BindingFlags.Public | BindingFlags.Instance);
        if (_submitReport == null)
            AppLogger.Log("WARNING: SubmitReport not found.");
        pad.FeedbackReceived += OnGameRumble;
        pad.Connect();
        _virtual = pad;

        // The virtual controller is whichever slot appears after connecting it.
        _virtualSlot = -1;
        for (int i = 0; i < 20 && _virtualSlot == -1; i++)
        {
            Thread.Sleep(100);
            var added = ConnectedSlots().Except(before).ToList();
            if (added.Count > 0) _virtualSlot = added.Min();
        }

        if (_virtualSlot == -1)
        {
            // Fallback: ViGEm virtual controllers have no XInput instance ID; physical ones do.
            _virtualSlot = ConnectedSlots().FirstOrDefault(s => FindXInputInstanceId(s) == null, -1);
            AppLogger.Log(_virtualSlot >= 0
                ? $"Virtual controller on slot {_virtualSlot} (found by instance ID)."
                : "WARNING: could not tell which slot the virtual controller is on.");
        }
        else
        {
            AppLogger.Log($"Virtual controller on slot {_virtualSlot}.");
        }
    }

    private long _nextSlotZeroCheck;

    /// <summary>
    /// If the virtual controller isn't on slot 0 and slot 0 has become free (e.g. the
    /// physical pad holding it was switched off), reconnect the virtual controller so it
    /// takes slot 0. The physical pad then gets a later slot when it comes back.
    /// Checked at most once a second on the poll thread.
    /// </summary>
    private void ReclaimSlotZero()
    {
        if (_virtualSlot <= 0 || _vigem == null) return; // already slot 0, or slot unknown
        long now = Environment.TickCount64;
        if (now < _nextSlotZeroCheck) return;
        _nextSlotZeroCheck = now + 1000;
        if (new Controller(UserIndex.One).IsConnected) return;

        AppLogger.Log($"Slot 0 is free; moving the virtual controller there from slot {_virtualSlot}.");
        var old = _virtual;
        _virtual = null;
        try
        {
            if (old != null)
            {
                old.FeedbackReceived -= OnGameRumble;
                WriteState(old, ControllerState.Neutral);
                _submitReport?.Invoke(old, null);
                old.Disconnect();
            }
            // Give XInput a moment to release the old slot.
            for (int i = 0; i < 10 && _virtualSlot >= 0 && new Controller((UserIndex)_virtualSlot).IsConnected; i++)
                Thread.Sleep(50);
            ConnectVirtual();
            if (_xinput != null && (int)_xinput.UserIndex == _virtualSlot) _xinput = null; // stale handle
            SlotChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Moving the virtual controller failed: {ex.Message}");
        }
    }

    /// <summary>Raised (on the poll thread) after the virtual controller moves to another slot.</summary>
    public event Action? SlotChanged;

    private static List<int> ConnectedSlots()
    {
        var slots = new List<int>();
        for (int i = 0; i < 4; i++)
            if (new Controller((UserIndex)i).IsConnected) slots.Add(i);
        return slots;
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
                ReclaimSlotZero();
                if (!TryReadInput(out var raw, out var source))
                {
                    SetSource(InputSource.None);
                    pollClock.Restart();
                    await Task.Delay(250, ct);
                    continue;
                }
                SetSource(source);

                double dt = Math.Clamp(pollClock.Elapsed.TotalSeconds, 0, 0.05);
                pollClock.Restart();

                // The physical pads are hidden, so the touch keyboard only sees the virtual pad.
                // While it's open, the controller drives the keyboard: the virtual pad stays
                // live (even when games are muted) and desktop mode pauses.
                bool keyboardNav = TouchKeyboard.IsVisible;
                if (keyboardNav != _keyboardNav)
                {
                    _keyboardNav = keyboardNav;
                    AppLogger.Log(keyboardNav
                        ? "Touch keyboard open: controller now drives the keyboard."
                        : "Touch keyboard closed: controller back to normal.");
                }

                lock (_desktopLock)
                {
                    if (keyboardNav) _desktop?.Suspend(ToPadState(raw));
                    else _desktop?.Update(ToPadState(raw), dt);
                }

                ApplyState(_isMuted && !keyboardNav ? ControllerState.Neutral : ForGames(raw));
            }
            catch (SharpDX.SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x8007001E))
            {
                _xinput = null;
                SetSource(InputSource.None);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLogger.Log($"Poll error: {ex.Message}");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private long _nextXInputScan;

    /// <summary>
    /// Reads both an XInput pad and a DualSense (if connected) and returns the raw state
    /// (no deadzone) of the one in use. See <see cref="ChooseSource"/>.
    /// </summary>
    private bool TryReadInput(out ControllerState raw, out InputSource source)
    {
        // Scanning empty XInput slots is slow, so look for a new pad at most once a second.
        if ((_xinput == null || !_xinput.IsConnected) && Environment.TickCount64 >= _nextXInputScan)
        {
            _xinput = FindPhysicalController();
            _nextXInputScan = Environment.TickCount64 + 1000;
        }

        ControllerState xiRaw = default, dsRaw = default;
        bool xiOk = false;
        if (_xinput != null && _xinput.IsConnected)
        {
            _xinput.GetState(out var state);
            xiRaw = FromGamepad(state.Gamepad);
            xiOk = true;
        }
        bool dsOk = _dualSense?.TryGetState(out dsRaw) == true;

        // Remember when each controller became available, so the first one wins.
        long now = Environment.TickCount64;
        if (xiOk && !_xiWasOk) _xiSince = now;
        if (dsOk && !_dsWasOk) _dsSince = now;
        _xiWasOk = xiOk;
        _dsWasOk = dsOk;

        source = ChooseSource(_source, xiOk, _xiSince, dsOk, _dsSince);
        raw = source switch
        {
            InputSource.XInput => xiRaw,
            InputSource.DualSense => dsRaw,
            _ => default,
        };
        return source != InputSource.None;
    }

    private bool _xiWasOk, _dsWasOk;
    private long _xiSince, _dsSince;

    /// <summary>
    /// Picks the controller in control when an Xbox pad and a DualSense may both be
    /// connected. The one in control keeps it until it disconnects, so two people can't
    /// fight over it; then the other takes over. If neither is in control, the one that
    /// connected first wins (Xbox on a tie).
    /// </summary>
    internal static InputSource ChooseSource(InputSource current,
        bool xiOk, long xiSince, bool dsOk, long dsSince)
    {
        if (current == InputSource.XInput && xiOk) return InputSource.XInput;
        if (current == InputSource.DualSense && dsOk) return InputSource.DualSense;
        if (xiOk && dsOk) return dsSince < xiSince ? InputSource.DualSense : InputSource.XInput;
        if (xiOk) return InputSource.XInput;
        return dsOk ? InputSource.DualSense : InputSource.None;
    }

    /// <summary>Handles a controller connecting, disconnecting, or switching.</summary>
    private void SetSource(InputSource source)
    {
        var old = _source;
        if (source == old) return;
        _source = source;

        if (old == InputSource.XInput) SetPhysicalRumble(_xinput, 0, 0);
        // A run that was killed mid-rumble leaves the motors running; start from silence.
        if (source == InputSource.XInput) SetPhysicalRumble(_xinput, 0, 0);
        ApplyState(ControllerState.Neutral); // nothing stays pressed for games

        lock (_desktopLock)
        {
            _desktop?.Reset();
            // A newly connected controller always starts in controller (game) mode.
            if (source != InputSource.None) _desktop?.SetActive(false);
        }

        AppLogger.Log(source switch
        {
            InputSource.DualSense => "Using DualSense.",
            InputSource.XInput => $"Using Xbox controller on slot {(int)_xinput!.UserIndex}.",
            _ => "Controller disconnected. Waiting for reconnect...",
        });
    }

    /// <summary>State sent to games: deadzone applied, Guide never passed through.</summary>
    internal static ControllerState ForGames(ControllerState raw) => raw with
    {
        LeftThumbX = ApplyDeadzone(raw.LeftThumbX),
        LeftThumbY = ApplyDeadzone(raw.LeftThumbY),
        RightThumbX = ApplyDeadzone(raw.RightThumbX),
        RightThumbY = ApplyDeadzone(raw.RightThumbY),
        Guide = false,
    };

    internal static ControllerState BuildReport(Gamepad gp) => ForGames(FromGamepad(gp));

    /// <summary>Raw XInput state as a <see cref="ControllerState"/> (no deadzone).</summary>
    internal static ControllerState FromGamepad(Gamepad gp)
    {
        var b = gp.Buttons;
        return new ControllerState(
            LeftThumbX:    gp.LeftThumbX,
            LeftThumbY:    gp.LeftThumbY,
            RightThumbX:   gp.RightThumbX,
            RightThumbY:   gp.RightThumbY,
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
        // Physical controllers deliberately stay hidden after exit (see HidHideManager): this
        // way nothing gets a chance to open them before CouchControl starts again. Turning off
        // "Manage HidHide" in the tray un-hides them.
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
