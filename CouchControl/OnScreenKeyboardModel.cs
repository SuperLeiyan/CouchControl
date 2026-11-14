namespace CouchControl;

internal enum OskAction { Char, Backspace, Enter, Space, Tab, Esc, Shift, Ctrl, Alt, Win, Page, Left, Right, Language }

/// <summary>
/// One key: label, what it does, width in key units, the character for Char keys, and the
/// corner character (typed by holding A, or by Shift on punctuation).
/// </summary>
internal sealed record OskKey(string Label, OskAction Action, double Width = 1, char Ch = '\0', char Alt = '\0');

/// <summary>
/// CouchControl's on-screen keyboard: layouts, highlight, modifiers and controller handling.
/// No UI code; <see cref="OnScreenKeyboardForm"/> draws it. Thread-safe: the poll thread calls
/// <see cref="Update"/>, the UI thread reads state under <see cref="Sync"/>.
///
/// Layout follows Windows' touch keyboard (TabTip). Controller:
///   D-pad / left stick  move the highlight      A  press the key (hold: corner character)
///   B  close            X  Backspace            Y  Space            Menu/Options  Enter
///   LB / RB  cursor left / right (select text while Shift is on)
///   LS click  Shift (double press: Caps)        LT  Ctrl            RT  letters ⇄ symbols
///   Right stick  move the keyboard              RS click  resize mode (right stick resizes,
///                                                         click again to save)
/// </summary>
internal sealed class OnScreenKeyboardModel
{
    public enum ShiftMode { Off, Once, Caps }

    /// <summary>Every row is this many key units wide, so rows line up for D-pad navigation.</summary>
    public const double RowUnits = 13;

    public static readonly OskKey[][][] Pages = [Letters(), Symbols()];

    private static OskKey C(char c, char alt = '\0') => new(c.ToString(), OskAction.Char, 1, c, alt);
    private static OskKey[] Chars(string s) => s.Select(c => C(c)).ToArray();

    private static readonly OskKey Esc = new("Esc", OskAction.Esc, 1.5);
    private static readonly OskKey Backspace = new("⌫", OskAction.Backspace, 1.5);
    private static readonly OskKey TabKey = new("Tab", OskAction.Tab, 1.5);
    private static readonly OskKey EnterKey = new("↵", OskAction.Enter, 1.5);
    private static readonly OskKey ShiftKey = new("⇧", OskAction.Shift, 1.5);
    private static readonly OskKey CtrlKey = new("Ctrl", OskAction.Ctrl);
    private static readonly OskKey CursorLeft = new("‹", OskAction.Left);
    private static readonly OskKey CursorRight = new("›", OskAction.Right);
    private static readonly OskKey Language = new("ENG", OskAction.Language, 1.5);

    private static OskKey[][] Letters() =>
    [
        [Esc, .. "qwertyuiop".Select((c, i) => C(c, "1234567890"[i])), Backspace],
        [TabKey, .. Chars("asdfghjkl"), C('\'', '"'), EnterKey],
        [ShiftKey, .. Chars("zxcvbnm"), C(',', ';'), C('.', ':'), C('?', '!'), ShiftKey],
        [new("&123", OskAction.Page, 1.5), CtrlKey, new("Win", OskAction.Win), new("Alt", OskAction.Alt),
            new("Space", OskAction.Space, 5), CursorLeft, CursorRight, Language],
    ];

    private static OskKey[][] Symbols() =>
    [
        [Esc, .. Chars("1234567890"), Backspace],
        [TabKey, .. Chars("!@#$%^&*()"), EnterKey],
        [.. Chars("`~-_=+[]{}\\|/")],
        [new("ABC", OskAction.Page, 1.5), CtrlKey, C('<'), C('>'),
            new("Space", OskAction.Space, 5), CursorLeft, CursorRight, Language],
    ];

    // ── State (guarded by _lock) ─────────────────────────────────────────

    private readonly object _lock = new();
    public object Sync => _lock;

    public int Page { get; private set; }
    public int Row { get; private set; } = 1;
    public int Col { get; private set; } = 5;
    public ShiftMode Shift { get; private set; }
    public bool Ctrl { get; private set; }
    public bool AltKey { get; private set; }
    public bool Win { get; private set; }
    public bool PlayStationLabels { get; private set; }

    /// <summary>R3 toggles this: the right stick resizes instead of moving the keyboard.</summary>
    public bool Resizing { get; private set; }

    /// <summary>Right stick after the dead zone, -1..1 each axis, +Y = up. Read by the form's timer.</summary>
    public (float X, float Y) RightStick { get; private set; }

    /// <summary>Set by <see cref="KeyboardHost"/> on the UI thread; read by the poll thread.</summary>
    public volatile bool IsOpen;

    public event Action? Changed;
    public event Action? CloseRequested;

    [Flags] private enum Outcome { None = 0, Changed = 1, Close = 2 }

    public OskKey[] CurrentRow(int row) => Pages[Page][row];

    private bool Shifted => Shift != ShiftMode.Off;

    /// <summary>The character a Char key types right now (Shift: capitals, and the corner character on punctuation).</summary>
    private char CharOf(OskKey k)
    {
        if (!Shifted) return k.Ch;
        if (char.IsAsciiLetter(k.Ch)) return char.ToUpperInvariant(k.Ch);
        return k.Alt != '\0' && !char.IsAsciiLetterOrDigit(k.Ch) ? k.Alt : k.Ch;
    }

    /// <summary>The character typed by holding A on a Char key, or '\0'.</summary>
    private char HoldCharOf(OskKey k)
    {
        if (k.Alt == '\0') return '\0';
        return CharOf(k) == k.Alt ? k.Ch : k.Alt; // shifted punctuation: hold gives the plain one
    }

    /// <summary>Text shown on a key, taking Shift into account.</summary>
    public string LabelOf(OskKey k) => k.Action switch
    {
        OskAction.Char => CharOf(k).ToString(),
        OskAction.Shift when Shift == ShiftMode.Caps => "⇪",
        _ => k.Label,
    };

    /// <summary>Small top-right text: what holding A on the key types.</summary>
    public string CornerOf(OskKey k) => k.Action switch
    {
        OskAction.Char when HoldCharOf(k) is var c and not '\0' => c.ToString(),
        OskAction.Backspace => "Del",
        _ => "",
    };

    /// <summary>Small bottom-right text: the controller button that does the same thing.</summary>
    public string BadgeOf(OskKey k) => k.Action switch
    {
        OskAction.Backspace => PlayStationLabels ? "□" : "X",
        OskAction.Enter => PlayStationLabels ? "Options" : "☰",
        OskAction.Space => PlayStationLabels ? "△" : "Y",
        OskAction.Shift => PlayStationLabels ? "L3" : "LS",
        OskAction.Ctrl => PlayStationLabels ? "L2" : "LT",
        OskAction.Page => PlayStationLabels ? "R2" : "RT",
        OskAction.Left => PlayStationLabels ? "L1" : "LB",
        OskAction.Right => PlayStationLabels ? "R1" : "RB",
        _ => "",
    };

    public bool IsLatched(OskKey k) => k.Action switch
    {
        OskAction.Shift => Shifted,
        OskAction.Ctrl => Ctrl,
        OskAction.Alt => AltKey,
        OskAction.Win => Win,
        _ => false,
    };

    public string Hints()
    {
        if (Resizing)
            return PlayStationLabels
                ? "Resizing: right stick → bigger, ← smaller   R3 save"
                : "Resizing: right stick → bigger, ← smaller   RS save";
        return PlayStationLabels
            ? "✕ type (hold: corner key)   ○ close   right stick move   R3 resize   ⇧+L1/R1 select"
            : "A type (hold: corner key)   B close   right stick move   RS resize   ⇧+LB/RB select";
    }

    /// <summary>Call when the keyboard opens: buttons held right now won't fire.</summary>
    public void OnOpened()
    {
        lock (_lock)
        {
            _started = false;
            _aPending = null;
            Page = 0;
            Shift = ShiftMode.Off;
            Ctrl = AltKey = Win = false;
            Resizing = false;
            RightStick = (0, 0);
            Row = Math.Min(Row, Pages[0].Length - 1);
            Col = Math.Min(Col, Pages[0][Row].Length - 1);
        }
        Changed?.Invoke();
    }

    // ── Controller input (poll thread) ───────────────────────────────────

    private const int StickThreshold = 18000, RightStickDeadZone = 7000;
    private const byte TriggerPress = 140, TriggerRelease = 100;
    private const long HoldMs = 500, DoubleShiftMs = 400;
    private ushort _prev;
    private bool _started, _rtDown, _ltDown;
    private (int dx, int dy) _lastDir;
    private readonly Repeater _dir = new(), _backspace = new(), _left = new(), _right = new();
    private OskKey? _aPending;   // A is held on a key with a corner character
    private long _aDownAt;
    private long _now, _shiftOnceAt;

    public void Update(ControllerState s, long nowMs, bool playStation)
    {
        Outcome outcome = Outcome.None;
        lock (_lock)
        {
            _now = nowMs;
            if (PlayStationLabels != playStation)
            {
                PlayStationLabels = playStation;
                outcome |= Outcome.Changed;
            }

            ushort bits = ControllerBridge.ToPadState(s).Digital;
            bool rt = _rtDown ? s.RightTrigger > TriggerRelease : s.RightTrigger > TriggerPress;
            bool lt = _ltDown ? s.LeftTrigger > TriggerRelease : s.LeftTrigger > TriggerPress;
            RightStick = (Curve(s.RightThumbX), Curve(s.RightThumbY));

            if (!_started)
            {
                // Ignore whatever is already held when the keyboard opens.
                _started = true;
                _prev = bits;
                _rtDown = rt;
                _ltDown = lt;
                _lastDir = Direction(s, bits);
                _dir.Hold(_lastDir != (0, 0), nowMs);
                _backspace.Hold(Has(bits, PadButton.X), nowMs);
                _left.Hold(Has(bits, PadButton.LB), nowMs);
                _right.Hold(Has(bits, PadButton.RB), nowMs);
            }
            else
            {
                ushort pressed = (ushort)(bits & ~_prev);
                ushort released = (ushort)(_prev & ~bits);
                _prev = bits;
                bool Pressed(PadButton b) => Has(pressed, b);

                var dir = Direction(s, bits);
                if (dir != _lastDir) { _lastDir = dir; _dir.Reset(); }
                if (_dir.Tick(dir != (0, 0), nowMs)) { MoveHighlight(dir.dx, dir.dy); outcome |= Outcome.Changed; }

                outcome |= HandleA(Pressed(PadButton.A), Has(released, PadButton.A), nowMs);
                if (Pressed(PadButton.B)) outcome |= Outcome.Close;
                if (_backspace.Tick(Has(bits, PadButton.X), nowMs)) outcome |= Activate(Backspace);
                if (Pressed(PadButton.Y)) outcome |= Activate(new("Space", OskAction.Space));
                if (Pressed(PadButton.Start)) outcome |= Activate(EnterKey);
                if (_left.Tick(Has(bits, PadButton.LB), nowMs)) outcome |= Activate(CursorLeft);
                if (_right.Tick(Has(bits, PadButton.RB), nowMs)) outcome |= Activate(CursorRight);
                if (Pressed(PadButton.LS)) outcome |= Activate(ShiftKey);
                if (Pressed(PadButton.RS)) { Resizing = !Resizing; outcome |= Outcome.Changed; }
                if (rt && !_rtDown) outcome |= Activate(new("&123", OskAction.Page));
                if (lt && !_ltDown) outcome |= Activate(CtrlKey);
                _rtDown = rt;
                _ltDown = lt;
            }
        }
        Raise(outcome);
    }

    /// <summary>
    /// A on a key with a corner character types on release, or the corner character after
    /// <see cref="HoldMs"/>. Other keys fire on press.
    /// </summary>
    private Outcome HandleA(bool pressed, bool released, long now)
    {
        if (pressed)
        {
            var key = CurrentRow(Row)[Col];
            if (CornerOf(key) == "") return Activate(key);
            _aPending = key;
            _aDownAt = now;
            return Outcome.None;
        }
        if (_aPending is not { } pending) return Outcome.None;
        if (released) { _aPending = null; return Activate(pending); }
        if (now - _aDownAt >= HoldMs) { _aPending = null; return ActivateHold(pending); }
        return Outcome.None;
    }

    /// <summary>Mouse click on a key (UI thread). Right click types the corner character.</summary>
    public void ActivateAt(int row, int col, bool hold)
    {
        Outcome outcome;
        lock (_lock)
        {
            _now = Environment.TickCount64;
            Row = row;
            Col = col;
            var key = CurrentRow(row)[col];
            outcome = (hold && CornerOf(key) != "" ? ActivateHold(key) : Activate(key)) | Outcome.Changed;
        }
        Raise(outcome);
    }

    private void Raise(Outcome outcome)
    {
        if (outcome.HasFlag(Outcome.Changed)) Changed?.Invoke();
        if (outcome.HasFlag(Outcome.Close)) CloseRequested?.Invoke();
    }

    private static bool Has(ushort bits, PadButton b) => (bits & (1 << (int)b)) != 0;

    /// <summary>Stick axis to -1..1 with a dead zone and a squared curve for fine control.</summary>
    private static float Curve(short v)
    {
        int a = Math.Abs((int)v);
        if (a < RightStickDeadZone) return 0;
        float t = Math.Min(1f, (a - RightStickDeadZone) / (32767f - RightStickDeadZone));
        return Math.Sign(v) * t * t;
    }

    /// <summary>D-pad wins; otherwise the left stick's dominant axis. dy = -1 means up.</summary>
    private static (int dx, int dy) Direction(ControllerState s, ushort bits)
    {
        if (Has(bits, PadButton.DPadUp)) return (0, -1);
        if (Has(bits, PadButton.DPadDown)) return (0, 1);
        if (Has(bits, PadButton.DPadLeft)) return (-1, 0);
        if (Has(bits, PadButton.DPadRight)) return (1, 0);

        int x = s.LeftThumbX, y = s.LeftThumbY;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < StickThreshold) return (0, 0);
        return Math.Abs(x) >= Math.Abs(y) ? (Math.Sign(x), 0) : (0, -Math.Sign(y)); // XInput +Y = up
    }

    // ── Navigation ───────────────────────────────────────────────────────

    private double CenterX(int row, int col)
    {
        var keys = CurrentRow(row);
        double x = 0;
        for (int i = 0; i < col; i++) x += keys[i].Width;
        return x + keys[col].Width / 2;
    }

    private void MoveHighlight(int dx, int dy)
    {
        var page = Pages[Page];
        if (dy != 0)
        {
            int target = Math.Clamp(Row + dy, 0, page.Length - 1);
            if (target == Row) return;
            double cx = CenterX(Row, Col);
            Row = target;
            Col = NearestCol(target, cx);
        }
        else
        {
            int n = page[Row].Length;
            Col = (Col + dx + n) % n; // wrap within the row
        }
    }

    private int NearestCol(int row, double cx)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int c = 0; c < CurrentRow(row).Length; c++)
        {
            double d = Math.Abs(CenterX(row, c) - cx);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    // ── Typing ───────────────────────────────────────────────────────────

    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
        VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_DELETE = 0x2E, VK_LWIN = 0x5B,
        VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;

    private Outcome Activate(OskKey k)
    {
        switch (k.Action)
        {
            case OskAction.Char: TypeChar(CharOf(k)); break;
            case OskAction.Backspace: Tap(VK_BACK); break;
            case OskAction.Enter: Tap(VK_RETURN); break;
            case OskAction.Space: Tap(VK_SPACE); break;
            case OskAction.Tab: Tap(VK_TAB); break;
            case OskAction.Esc: Tap(VK_ESCAPE); break;
            case OskAction.Left: TapCursor(VK_LEFT); break;
            case OskAction.Right: TapCursor(VK_RIGHT); break;
            case OskAction.Shift: PressShift(); break;
            case OskAction.Ctrl: Ctrl = !Ctrl; break;
            case OskAction.Alt: AltKey = !AltKey; break;
            case OskAction.Win: Win = !Win; break;
            case OskAction.Page:
                Page ^= 1;
                Row = Math.Min(Row, Pages[Page].Length - 1);
                Col = Math.Min(Col, Pages[Page][Row].Length - 1);
                break;
            case OskAction.Language:
                AppLogger.Log("On-screen keyboard: other languages aren't supported yet.");
                return Outcome.None;
            default: return Outcome.None;
        }
        return Outcome.Changed;
    }

    /// <summary>Hold A: the corner character, or Delete on Backspace.</summary>
    private Outcome ActivateHold(OskKey k)
    {
        if (k.Action == OskAction.Backspace) { Tap(VK_DELETE); return Outcome.Changed; }
        char c = HoldCharOf(k);
        if (c == '\0') return Activate(k);
        TypeChar(c);
        return Outcome.Changed;
    }

    /// <summary>
    /// A press is one capital. A second press within <see cref="DoubleShiftMs"/> is Caps;
    /// a later press, or a press while on Caps, turns Shift off.
    /// </summary>
    private void PressShift()
    {
        switch (Shift)
        {
            case ShiftMode.Off:
                Shift = ShiftMode.Once;
                _shiftOnceAt = _now;
                break;
            case ShiftMode.Once:
                Shift = _now - _shiftOnceAt <= DoubleShiftMs ? ShiftMode.Caps : ShiftMode.Off;
                break;
            default:
                Shift = ShiftMode.Off;
                break;
        }
    }

    /// <summary>Types a character. With Ctrl/Alt/Win latched, letters and digits become shortcuts (e.g. Ctrl+C).</summary>
    private void TypeChar(char c)
    {
        if ((Ctrl || AltKey || Win) && char.IsAsciiLetterOrDigit(c))
        {
            var keys = Modifiers();
            if (Shifted) keys.Add(VK_LSHIFT);
            keys.Add((ushort)char.ToUpperInvariant(c));
            Send(keys);
        }
        else
        {
            InputSender.Instance.Text(c);
        }
        Ctrl = AltKey = Win = false;
        if (Shift == ShiftMode.Once) Shift = ShiftMode.Off;
    }

    /// <summary>
    /// Cursor left/right. While Shift is on (single or Caps), it selects text (Shift+arrow) and
    /// Shift stays on, so repeated presses extend the selection. With Ctrl too, it selects by word.
    /// </summary>
    private void TapCursor(ushort vk)
    {
        var keys = Modifiers();
        if (Shifted) keys.Add(VK_LSHIFT);
        keys.Add(vk);
        Send(keys);
        Ctrl = AltKey = Win = false;
    }

    /// <summary>Press and release a key with the latched modifiers (then unlatch them).</summary>
    private void Tap(ushort vk)
    {
        var keys = Modifiers();
        keys.Add(vk);
        Send(keys);
        Ctrl = AltKey = Win = false;
    }

    private List<ushort> Modifiers()
    {
        var keys = new List<ushort>(4);
        if (Ctrl) keys.Add(VK_LCONTROL);
        if (AltKey) keys.Add(VK_LMENU);
        if (Win) keys.Add(VK_LWIN);
        return keys;
    }

    private static void Send(List<ushort> keys)
    {
        InputSender.Instance.Keys(keys, true);
        InputSender.Instance.Keys(keys, false);
    }

    /// <summary>Fires on press, then repeats after 400 ms every 70 ms while held.</summary>
    private sealed class Repeater
    {
        private bool _held;
        private long _next;

        public void Reset() => _held = false;

        /// <summary>Marks the current state as already handled (no fire for a held button).</summary>
        public void Hold(bool down, long now)
        {
            _held = down;
            _next = long.MaxValue; // a button held at open doesn't repeat until re-pressed
        }

        public bool Tick(bool down, long now)
        {
            if (!down) { _held = false; return false; }
            if (!_held) { _held = true; _next = now + 400; return true; }
            if (now >= _next) { _next = now + 70; return true; }
            return false;
        }
    }
}
