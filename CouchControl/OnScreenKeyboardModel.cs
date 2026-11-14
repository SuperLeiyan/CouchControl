namespace CouchControl;

internal enum OskAction { Char, Backspace, Delete, Enter, Space, Tab, Shift, Ctrl, Page, Left, Right, Move, Close }

/// <summary>One key: label, what it does, width in key units, and the character for Char keys.</summary>
internal sealed record OskKey(string Label, OskAction Action, double Width = 1, char Ch = '\0');

/// <summary>
/// CouchControl's on-screen keyboard: layouts, highlight, modifiers and controller handling.
/// No UI code; <see cref="OnScreenKeyboardForm"/> draws it. Thread-safe: the poll thread calls
/// <see cref="Update"/>, the UI thread reads state under <see cref="Sync"/>.
///
/// Controller layout follows Windows' gamepad keyboard:
///   D-pad / left stick  move the highlight      A  press the key        B  close
///   X  Backspace        Y  Space                Menu/Options  Enter
///   LB / RB  cursor left / right                LS click  Shift (tap again for Caps)
///   RT  letters ⇄ symbols                       LT  Ctrl (for Ctrl+C, Ctrl+V, …)
///   RS click  move the keyboard between the bottom and top of the screen
/// </summary>
internal sealed class OnScreenKeyboardModel
{
    public enum ShiftMode { Off, Once, Caps }

    /// <summary>Every row is this many key units wide, so rows line up for D-pad navigation.</summary>
    public const double RowUnits = 11.5;

    public static readonly OskKey[][][] Pages = [Letters(), Symbols()];

    private static OskKey C(char c) => new(c.ToString(), OskAction.Char, 1, c);
    private static OskKey[] Chars(string s) => s.Select(C).ToArray();

    private static OskKey[] BottomRow(string pageLabel) =>
    [
        new(pageLabel, OskAction.Page, 1.5), new("Ctrl", OskAction.Ctrl, 1.25), new("←", OskAction.Left),
        new("Space", OskAction.Space, 4.5), new("→", OskAction.Right), new("⇅", OskAction.Move),
        new("✕", OskAction.Close, 1.25),
    ];

    private static OskKey[][] Letters() =>
    [
        [.. Chars("1234567890"), new("⌫", OskAction.Backspace, 1.5)],
        [new("Tab", OskAction.Tab, 1.5), .. Chars("qwertyuiop")],
        [.. Chars("asdfghjkl'"), new("Enter", OskAction.Enter, 1.5)],
        [new("⇧", OskAction.Shift, 1.5), .. Chars("zxcvbnm,.?")],
        BottomRow("&123"),
    ];

    private static OskKey[][] Symbols() =>
    [
        [.. Chars("1234567890"), new("⌫", OskAction.Backspace, 1.5)],
        [new("Tab", OskAction.Tab, 1.5), .. Chars("!@#$%^&*()")],
        [.. Chars("-_=+[]{}\\|"), new("Enter", OskAction.Enter, 1.5)],
        [new("Del", OskAction.Delete, 1.5), .. Chars("`~;:\"<>/,.")],
        BottomRow("ABC"),
    ];

    // ── State (guarded by _lock) ─────────────────────────────────────────

    private readonly object _lock = new();
    public object Sync => _lock;

    public int Page { get; private set; }
    public int Row { get; private set; } = 2;
    public int Col { get; private set; } = 4;
    public ShiftMode Shift { get; private set; }
    public bool Ctrl { get; private set; }
    public bool DockTop { get; private set; }
    public bool PlayStationLabels { get; private set; }

    /// <summary>Set by <see cref="KeyboardHost"/> on the UI thread; read by the poll thread.</summary>
    public volatile bool IsOpen;

    public event Action? Changed;
    public event Action? CloseRequested;
    public event Action? MoveRequested;

    [Flags] private enum Outcome { None = 0, Changed = 1, Close = 2, Move = 4 }

    public OskKey[] CurrentRow(int row) => Pages[Page][row];

    /// <summary>Text shown on a key, taking Shift into account.</summary>
    public string LabelOf(OskKey k)
        => k.Action == OskAction.Char && char.IsAsciiLetter(k.Ch) && Shift != ShiftMode.Off
            ? k.Label.ToUpperInvariant()
            : k.Label;

    public string Hints() => PlayStationLabels
        ? "✕ type   ○ close   □ ⌫   △ space   Options enter   L1/R1 ← →   L3 shift   L2 ctrl   R2 &123   R3 move"
        : "A type   B close   X ⌫   Y space   ☰ enter   LB/RB ← →   LS shift   LT ctrl   RT &123   RS move";

    /// <summary>Call when the keyboard opens: buttons held right now won't fire.</summary>
    public void OnOpened()
    {
        lock (_lock)
        {
            _started = false;
            Page = 0;
            Shift = ShiftMode.Off;
            Ctrl = false;
            Row = Math.Min(Row, Pages[0].Length - 1);
            Col = Math.Min(Col, Pages[0][Row].Length - 1);
        }
        Changed?.Invoke();
    }

    // ── Controller input (poll thread) ───────────────────────────────────

    private const int StickThreshold = 18000;
    private const byte TriggerPress = 140, TriggerRelease = 100;
    private ushort _prev;
    private bool _started, _rtDown, _ltDown;
    private (int dx, int dy) _lastDir;
    private readonly Repeater _dir = new(), _backspace = new(), _left = new(), _right = new();

    public void Update(ControllerState s, long nowMs, bool playStation)
    {
        Outcome outcome = Outcome.None;
        lock (_lock)
        {
            if (PlayStationLabels != playStation)
            {
                PlayStationLabels = playStation;
                outcome |= Outcome.Changed;
            }

            ushort bits = ControllerBridge.ToPadState(s).Digital;
            bool rt = _rtDown ? s.RightTrigger > TriggerRelease : s.RightTrigger > TriggerPress;
            bool lt = _ltDown ? s.LeftTrigger > TriggerRelease : s.LeftTrigger > TriggerPress;

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
                _prev = bits;
                bool Pressed(PadButton b) => Has(pressed, b);

                var dir = Direction(s, bits);
                if (dir != _lastDir) { _lastDir = dir; _dir.Reset(); }
                if (_dir.Tick(dir != (0, 0), nowMs)) { MoveHighlight(dir.dx, dir.dy); outcome |= Outcome.Changed; }

                if (Pressed(PadButton.A)) outcome |= Activate(CurrentRow(Row)[Col]);
                if (Pressed(PadButton.B)) outcome |= Outcome.Close;
                if (_backspace.Tick(Has(bits, PadButton.X), nowMs)) outcome |= Activate(new("⌫", OskAction.Backspace));
                if (Pressed(PadButton.Y)) outcome |= Activate(new("Space", OskAction.Space));
                if (Pressed(PadButton.Start)) outcome |= Activate(new("Enter", OskAction.Enter));
                if (_left.Tick(Has(bits, PadButton.LB), nowMs)) outcome |= Activate(new("←", OskAction.Left));
                if (_right.Tick(Has(bits, PadButton.RB), nowMs)) outcome |= Activate(new("→", OskAction.Right));
                if (Pressed(PadButton.LS)) outcome |= Activate(new("⇧", OskAction.Shift));
                if (Pressed(PadButton.RS)) outcome |= Outcome.Move;
                if (rt && !_rtDown) outcome |= Activate(new("&123", OskAction.Page));
                if (lt && !_ltDown) outcome |= Activate(new("Ctrl", OskAction.Ctrl));
                _rtDown = rt;
                _ltDown = lt;
            }
        }
        Raise(outcome);
    }

    /// <summary>Mouse click on a key (UI thread).</summary>
    public void ActivateAt(int row, int col)
    {
        Outcome outcome;
        lock (_lock)
        {
            Row = row;
            Col = col;
            outcome = Activate(CurrentRow(row)[col]) | Outcome.Changed;
        }
        Raise(outcome);
    }

    private void Raise(Outcome outcome)
    {
        if (outcome.HasFlag(Outcome.Move))
        {
            lock (_lock) DockTop = !DockTop;
            MoveRequested?.Invoke();
        }
        if (outcome.HasFlag(Outcome.Changed) || outcome.HasFlag(Outcome.Move)) Changed?.Invoke();
        if (outcome.HasFlag(Outcome.Close)) CloseRequested?.Invoke();
    }

    private static bool Has(ushort bits, PadButton b) => (bits & (1 << (int)b)) != 0;

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

    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20,
        VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_DELETE = 0x2E, VK_LCONTROL = 0xA2;

    private Outcome Activate(OskKey k)
    {
        switch (k.Action)
        {
            case OskAction.Char:
                if (Ctrl && char.IsAsciiLetterOrDigit(k.Ch))
                {
                    Tap((ushort)char.ToUpperInvariant(k.Ch)); // Ctrl+letter, e.g. Ctrl+C
                }
                else
                {
                    InputSender.Instance.Text(LabelOf(k)[0]);
                    Ctrl = false;
                }
                if (Shift == ShiftMode.Once) Shift = ShiftMode.Off;
                return Outcome.Changed;
            case OskAction.Backspace: Tap(VK_BACK); return Outcome.Changed;
            case OskAction.Delete: Tap(VK_DELETE); return Outcome.Changed;
            case OskAction.Enter: Tap(VK_RETURN); return Outcome.Changed;
            case OskAction.Space: Tap(VK_SPACE); return Outcome.Changed;
            case OskAction.Tab: Tap(VK_TAB); return Outcome.Changed;
            case OskAction.Left: Tap(VK_LEFT); return Outcome.Changed;
            case OskAction.Right: Tap(VK_RIGHT); return Outcome.Changed;
            case OskAction.Shift:
                Shift = Shift switch { ShiftMode.Off => ShiftMode.Once, ShiftMode.Once => ShiftMode.Caps, _ => ShiftMode.Off };
                return Outcome.Changed;
            case OskAction.Ctrl:
                Ctrl = !Ctrl;
                return Outcome.Changed;
            case OskAction.Page:
                Page ^= 1;
                Row = Math.Min(Row, Pages[Page].Length - 1);
                Col = Math.Min(Col, Pages[Page][Row].Length - 1);
                return Outcome.Changed;
            case OskAction.Move: return Outcome.Move;
            case OskAction.Close: return Outcome.Close;
            default: return Outcome.None;
        }
    }

    /// <summary>Press and release a key, with Ctrl if it's latched (then unlatch it).</summary>
    private void Tap(ushort vk)
    {
        ushort[] keys = Ctrl ? [VK_LCONTROL, vk] : [vk];
        InputSender.Instance.Keys(keys, true);
        InputSender.Instance.Keys(keys, false);
        Ctrl = false;
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
