namespace CouchControl;

public enum PadButton { A, B, X, Y, LB, RB, LT, RT, Back, Start, LS, RS, DPadUp, DPadDown, DPadLeft, DPadRight }

/// <summary>Raw controller snapshot fed to <see cref="DesktopMapper"/> (no deadzone applied).</summary>
internal readonly record struct PadState(
    short LeftX, short LeftY, short RightX, short RightY,
    byte LeftTrigger, byte RightTrigger, ushort Digital /* bit per PadButton, triggers excluded */);

/// <summary>
/// Built-in desktop mode (replaces JoyXoff). While active, sticks drive the cursor and
/// scroll wheel, and buttons send mouse clicks / key combos. A button chord (default
/// Back+Start) switches between game mode and desktop mode at any time.
///
/// Pure apart from the injected <see cref="IInputSink"/>, so it's unit-testable.
/// </summary>
internal sealed class DesktopMapper
{
    private const int TriggerPress = 140, TriggerRelease = 100;
    private const double ScrollEmitThreshold = 30; // wheel units; 120 = one notch

    private readonly IInputSink _sink;
    private readonly Action<string> _oneShot;
    private DesktopModeSettings _settings = new();
    private readonly ButtonAction?[] _actions = new ButtonAction?[16];
    private uint _chordMask;

    private uint _prev;          // pressed mask from the previous update
    private bool _chordLatched;  // chord currently held (fires once per hold)
    private bool _chordUsed;     // chord fired during the current hold of its buttons
    private bool _triggerL, _triggerR;
    private uint _held;          // buttons whose press action is currently held down
    private double _mouseRemX, _mouseRemY, _scrollAccV, _scrollAccH;

    public bool IsActive { get; private set; }
    public event Action<bool>? ModeChanged;

    public DesktopMapper(IInputSink sink, Action<string> oneShot, DesktopModeSettings settings)
    {
        _sink = sink;
        _oneShot = oneShot;
        ApplySettings(settings);
        IsActive = false; // always start in controller (game) mode
    }

    public void ApplySettings(DesktopModeSettings settings)
    {
        ReleaseAll();
        _settings = settings;
        var lookup = new Dictionary<string, string>(settings.Buttons, StringComparer.OrdinalIgnoreCase);
        foreach (PadButton b in Enum.GetValues<PadButton>())
            _actions[(int)b] = lookup.TryGetValue(b.ToString(), out var a) ? ButtonAction.Parse(a) : null;
        _chordMask = ParseChord(settings.ToggleChord);
        if (!settings.Enabled) SetActive(false);
    }

    internal static uint ParseChord(string? chord)
    {
        uint mask = 0;
        foreach (var part in (chord ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<PadButton>(part, ignoreCase: true, out var b)) mask |= 1u << (int)b;
        // A single-button "chord" would make that button unusable in games; require two.
        return System.Numerics.BitOperations.PopCount(mask) >= 2 ? mask : 0;
    }

    public void SetActive(bool active)
    {
        if (active && !_settings.Enabled) active = false;
        if (active == IsActive) return;
        if (!active) ReleaseAll();
        IsActive = active;
        ModeChanged?.Invoke(active);
    }

    /// <summary>Call when the controller disconnects so nothing stays held.</summary>
    public void Reset()
    {
        ReleaseAll();
        _prev = 0;
        _chordLatched = _chordUsed = false;
        _triggerL = _triggerR = false;
    }

    public void Update(PadState s, double dtSeconds)
    {
        uint pressed = PressedMask(s);

        // ── Mode toggle chord (works in both modes) ──
        if (_chordMask != 0)
        {
            bool chordDown = (pressed & _chordMask) == _chordMask;
            if (chordDown && !_chordLatched && _settings.Enabled)
            {
                _chordLatched = true;
                _chordUsed = true;
                SetActive(!IsActive);
                _prev = pressed; // don't treat buttons already held as new presses
                return;
            }
            if (!chordDown) _chordLatched = false;
        }

        if (!IsActive)
        {
            _prev = pressed;
            if ((pressed & _chordMask) == 0) _chordUsed = false;
            return;
        }

        // ── Buttons ──
        uint changed = pressed ^ _prev;
        for (int i = 0; i < 16; i++)
        {
            uint bit = 1u << i;
            if ((changed & bit) == 0) continue;
            bool down = (pressed & bit) != 0;
            var action = _actions[i];
            if (action == null) continue;

            if ((_chordMask & bit) != 0)
            {
                // Chord buttons act on release (tap), unless the chord was used this hold.
                if (!down && !_chordUsed) Tap(action);
                continue;
            }

            if (down) Begin(i, action);
            else End(i, action);
        }
        if ((pressed & _chordMask) == 0) _chordUsed = false;
        _prev = pressed;

        // ── Sticks ──
        ApplyStick(_settings.LeftStick, s.LeftX, s.LeftY, dtSeconds);
        ApplyStick(_settings.RightStick, s.RightX, s.RightY, dtSeconds);
    }

    private uint PressedMask(PadState s)
    {
        // Triggers become digital buttons with hysteresis.
        _triggerL = _triggerL ? s.LeftTrigger > TriggerRelease : s.LeftTrigger > TriggerPress;
        _triggerR = _triggerR ? s.RightTrigger > TriggerRelease : s.RightTrigger > TriggerPress;
        uint mask = s.Digital & ~((1u << (int)PadButton.LT) | (1u << (int)PadButton.RT));
        if (_triggerL) mask |= 1u << (int)PadButton.LT;
        if (_triggerR) mask |= 1u << (int)PadButton.RT;
        return mask;
    }

    private void Begin(int index, ButtonAction action)
    {
        switch (action)
        {
            case ButtonAction.Mouse m: _sink.MouseButton(m.Button, true); _held |= 1u << index; break;
            case ButtonAction.Keys k: _sink.Keys(k.VirtualKeys, true); _held |= 1u << index; break;
            case ButtonAction.OneShot o: _oneShot(o.Name); break;
        }
    }

    private void End(int index, ButtonAction action)
    {
        uint bit = 1u << index;
        if ((_held & bit) == 0) return;
        _held &= ~bit;
        switch (action)
        {
            case ButtonAction.Mouse m: _sink.MouseButton(m.Button, false); break;
            case ButtonAction.Keys k: _sink.Keys(k.VirtualKeys, false); break;
        }
    }

    private void Tap(ButtonAction action)
    {
        switch (action)
        {
            case ButtonAction.Mouse m: _sink.MouseButton(m.Button, true); _sink.MouseButton(m.Button, false); break;
            case ButtonAction.Keys k: _sink.Keys(k.VirtualKeys, true); _sink.Keys(k.VirtualKeys, false); break;
            case ButtonAction.OneShot o: _oneShot(o.Name); break;
        }
    }

    private void ReleaseAll()
    {
        for (int i = 0; i < 16; i++)
            if ((_held & (1u << i)) != 0 && _actions[i] is { } a) End(i, a);
        _held = 0;
        _mouseRemX = _mouseRemY = _scrollAccV = _scrollAccH = 0;
    }

    private void ApplyStick(string mode, short rawX, short rawY, double dt)
    {
        bool mouse = mode.Equals("Mouse", StringComparison.OrdinalIgnoreCase);
        bool scroll = mode.Equals("Scroll", StringComparison.OrdinalIgnoreCase);
        if (!mouse && !scroll) return;

        var (x, y) = Shape(rawX, rawY, _settings.StickDeadzone, _settings.PointerCurve);
        if (x == 0 && y == 0)
        {
            if (mouse) { _mouseRemX = _mouseRemY = 0; }
            else { _scrollAccV = _scrollAccH = 0; }
            return;
        }

        if (mouse)
        {
            _mouseRemX += x * _settings.PointerSpeed * dt;
            _mouseRemY += -y * _settings.PointerSpeed * dt; // screen Y grows downward
            int dx = (int)Math.Truncate(_mouseRemX), dy = (int)Math.Truncate(_mouseRemY);
            _mouseRemX -= dx; _mouseRemY -= dy;
            _sink.MouseMove(dx, dy);
        }
        else
        {
            _scrollAccV += y * _settings.ScrollSpeed * dt; // stick up = scroll up (positive wheel)
            _scrollAccH += x * _settings.ScrollSpeed * dt; // stick right = scroll right
            if (Math.Abs(_scrollAccV) >= ScrollEmitThreshold)
            {
                int d = (int)_scrollAccV; _scrollAccV -= d; _sink.Wheel(d, horizontal: false);
            }
            if (Math.Abs(_scrollAccH) >= ScrollEmitThreshold)
            {
                int d = (int)_scrollAccH; _scrollAccH -= d; _sink.Wheel(d, horizontal: true);
            }
        }
    }

    /// <summary>
    /// Radial deadzone + response curve. Returns a direction scaled to [0, 1] magnitude,
    /// with +Y = stick up.
    /// </summary>
    internal static (double x, double y) Shape(short rawX, short rawY, double deadzone, double curve)
    {
        double x = Math.Max(-1, rawX / 32767.0), y = Math.Max(-1, rawY / 32767.0);
        double m = Math.Sqrt(x * x + y * y);
        deadzone = Math.Clamp(deadzone, 0, 0.9);
        if (m <= deadzone) return (0, 0);
        double norm = Math.Min(1, (m - deadzone) / (1 - deadzone));
        double scaled = Math.Pow(norm, Math.Max(0.1, curve));
        return (x / m * scaled, y / m * scaled);
    }
}
