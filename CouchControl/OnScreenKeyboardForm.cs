using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CouchControl;

/// <summary>
/// Draws <see cref="OnScreenKeyboardModel"/>. A topmost window that never takes focus, so keys
/// go to the app you're typing in. Mouse clicks on keys work too (right click: corner character).
/// The right stick moves it for the current session. In edit mode (R3) the right stick moves it,
/// the left stick resizes it, D-pad ↑/↓ changes transparency and ←/→ makes it narrower/wider.
/// Leaving edit mode saves all of it in <see cref="KeyboardSettings"/>. Keys scale to fit.
/// </summary>
internal sealed class OnScreenKeyboardForm : Form
{
    private readonly OnScreenKeyboardModel _model;
    private readonly KeyboardSettings _settings;
    private readonly Action _save;
    private readonly Dictionary<int, Font> _fonts = new();
    private readonly System.Windows.Forms.Timer _stickTimer = new() { Interval = 16 };
    private readonly Stopwatch _clock = new();

    private static readonly Color Background = Color.FromArgb(28, 28, 28);
    private static readonly Color BarColor = Color.FromArgb(36, 36, 36);
    private static readonly Color KeyColor = Color.FromArgb(58, 58, 58);
    private static readonly Color SpecialKeyColor = Color.FromArgb(44, 44, 44);
    private static readonly Color Accent = Color.FromArgb(0, 120, 215);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
    private static readonly Color HintColor = Color.FromArgb(160, 160, 160);

    private const float PadRatio = 0.010f, KeyHeightRatio = 0.80f, BarRatio = 0.5f;
    private const int MinWidth = 480;
    private const double MovePxPerSec = 1400, ResizePxPerSec = 900, StretchPxPerSec = 700, OpacityPerSec = 0.6;
    private const double MinOpacity = 0.3, MaxOpacity = 1.0;
    private const double MinAspect = 1.8, MaxAspect = 7.0; // width ÷ height, for D-pad stretching

    // Sub-pixel position/size while the sticks move it.
    private double _x, _y, _w, _h;
    private bool _stickActive, _layoutDirty, _wasEditing;
    private Point? _dragOffset;

    public OnScreenKeyboardForm(OnScreenKeyboardModel model, KeyboardSettings settings, Action save)
    {
        _model = model;
        _settings = settings;
        _save = save;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Opacity = Math.Clamp(_settings.Opacity, MinOpacity, MaxOpacity);
        Text = "CouchControl Keyboard";
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _model.Changed += () => { if (IsHandleCreated) BeginInvoke(Invalidate); };
        _stickTimer.Tick += (_, _) => OnStickTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE; // clicking a key must not steal focus
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _wasEditing = false;
            _stickActive = false;
            _layoutDirty = false;
            _clock.Restart();
            _stickTimer.Start();
        }
        else
        {
            _stickTimer.Stop();
            if (_wasEditing) SaveLayout(); // closed while editing: keep the edit
            _layoutDirty = false;
        }
    }

    /// <summary>
    /// Puts the keyboard where it was last saved, or docks it at the bottom of the monitor of the
    /// window being typed in.
    /// </summary>
    public void PlaceOnScreen()
    {
        Opacity = Math.Clamp(_settings.Opacity, MinOpacity, MaxOpacity);
        if (_settings.Width >= MinWidth && _settings.Left is int left && _settings.Top is int top)
        {
            int h = _settings.Height > 0 ? _settings.Height : HeightFor(_settings.Width);
            var saved = new Rectangle(left, top, _settings.Width, h);
            var wa = Screen.FromRectangle(saved).WorkingArea;
            if (wa.IntersectsWith(saved))
            {
                SetLayout(saved.X, saved.Y, saved.Width, saved.Height, wa);
                return;
            }
        }

        IntPtr fg = GetForegroundWindow();
        var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        int width = Math.Min((int)(area.Width * 0.6), 1600);
        if (HeightFor(width) > area.Height * 0.45)
            width = (int)(width * area.Height * 0.45 / HeightFor(width));
        int height = HeightFor(width);
        int margin = (int)(area.Height * 0.015);
        SetLayout(area.Left + (area.Width - width) / 2, area.Bottom - height - margin, width, height, area);
    }

    /// <summary>Applies a position and size, kept inside the working area.</summary>
    private void SetLayout(double x, double y, double width, double height, Rectangle wa)
    {
        width = Math.Clamp(width, Math.Min(MinWidth, wa.Width), wa.Width);
        height = Math.Clamp(height, Math.Min(120, wa.Height), wa.Height);
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));
        _x = x; _y = y; _w = width; _h = height;
        Bounds = new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));
    }

    private void OnStickTick()
    {
        double dt = Math.Min(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();

        float mx, my, rx, ry;
        int px, py;
        bool editing;
        lock (_model.Sync)
        {
            (mx, my) = _model.RightStick;
            (rx, ry) = _model.LeftStick; // only non-zero in edit mode
            (px, py) = _model.EditDPad;  // only non-zero in edit mode
            editing = _model.Editing;
        }

        if (editing && !_wasEditing) _layoutDirty = true; // R3 then R3 keeps the current spot too
        if (_wasEditing && !editing) SaveLayout();        // R3 again: save size, position, transparency
        _wasEditing = editing;

        if (mx == 0 && my == 0 && rx == 0 && ry == 0 && px == 0 && py == 0)
        {
            _stickActive = false;
            return;
        }

        if (!_stickActive)
        {
            // Start from the real bounds (Windows may have moved/resized us, e.g. a DPI change).
            _x = Left; _y = Top; _w = Width; _h = Height;
            _stickActive = true;
        }

        var wa = Screen.FromRectangle(Bounds).WorkingArea;

        if (py != 0) // D-pad up: more solid, down: more see-through
            Opacity = Math.Clamp(Opacity - py * OpacityPerSec * dt, MinOpacity, MaxOpacity);

        if (rx != 0 || ry != 0 || px != 0)
        {
            double cx = _x + _w / 2, cy = _y + _h / 2;
            double w = _w, h = _h;
            if (rx != 0 || ry != 0)
            {
                // Left stick right / up grows, left / down shrinks, keeping the shape.
                float grow = Math.Abs(rx) >= Math.Abs(ry) ? rx : ry;
                double newW = Math.Clamp(w + grow * ResizePxPerSec * dt, Math.Min(MinWidth, wa.Width), wa.Width);
                double newH = h * newW / w;
                if (newH > wa.Height) { newW *= wa.Height / newH; newH = wa.Height; }
                w = newW; h = newH;
            }
            if (px != 0)
            {
                // D-pad left / right: narrower / wider, same height.
                w = Math.Clamp(w + px * StretchPxPerSec * dt,
                    Math.Max(Math.Min(MinWidth, wa.Width), h * MinAspect), Math.Min(wa.Width, h * MaxAspect));
            }
            SetLayout(cx - w / 2, cy - h / 2, w, h, wa); // the centre stays put
        }

        if (mx != 0 || my != 0)
        {
            // Allow crossing to another monitor: clamp to the whole desktop while moving.
            var desk = SystemInformation.VirtualScreen;
            double nx = Math.Clamp(_x + mx * MovePxPerSec * dt, desk.Left, desk.Right - _w);
            double ny = Math.Clamp(_y - my * MovePxPerSec * dt, desk.Top, desk.Bottom - _h); // XInput +Y = up
            _x = nx; _y = ny;
            Location = new Point((int)Math.Round(nx), (int)Math.Round(ny));
        }
        if (editing) _layoutDirty = true; // outside edit mode, moves last only until it closes
    }

    /// <summary>Saves the current size and position if they changed.</summary>
    private void SaveLayout()
    {
        if (!_layoutDirty) return;
        _layoutDirty = false;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Opacity = Math.Round(Opacity, 2);
        _settings.Left = Left;
        _settings.Top = Top;
        _save();
    }

    private static int HeightFor(int width)
    {
        float pad = width * PadRatio;
        float unit = (width - 2 * pad) / (float)OnScreenKeyboardModel.RowUnits;
        float keyH = unit * KeyHeightRatio;
        int rows = OnScreenKeyboardModel.Pages[0].Length;
        return (int)(2 * pad + keyH * BarRatio + rows * keyH);
    }

    /// <summary>Key width comes from the window width and key height from its height, so keys stretch with it.</summary>
    private (float pad, float unit, float keyH, float barH) Metrics()
    {
        int rows = OnScreenKeyboardModel.Pages[0].Length;
        float pad = Math.Min(ClientSize.Width * PadRatio, ClientSize.Height * 0.03f);
        float unit = (ClientSize.Width - 2 * pad) / (float)OnScreenKeyboardModel.RowUnits;
        float keyH = Math.Max(1, (ClientSize.Height - 2 * pad) / (rows + BarRatio));
        return (pad, unit, keyH, keyH * BarRatio);
    }

    private RectangleF BarRect()
    {
        var (pad, _, _, barH) = Metrics();
        return new RectangleF(0, 0, ClientSize.Width, pad + barH);
    }

    private RectangleF CloseRect()
    {
        var (pad, _, _, barH) = Metrics();
        return new RectangleF(ClientSize.Width - pad - barH * 1.6f, pad / 2, barH * 1.6f, barH);
    }

    /// <summary>Key rectangles of the current page (call under the model lock).</summary>
    private IEnumerable<(int row, int col, OskKey key, RectangleF rect)> KeyRects()
    {
        var (pad, unit, keyH, barH) = Metrics();
        float gap = Math.Min(unit, keyH) * 0.08f;
        float top = pad + barH;
        var page = OnScreenKeyboardModel.Pages[_model.Page];
        for (int r = 0; r < page.Length; r++)
        {
            float x = pad;
            for (int c = 0; c < page[r].Length; c++)
            {
                var k = page[r][c];
                float w = (float)k.Width * unit;
                yield return (r, c, k, new RectangleF(x + gap / 2, top + r * keyH + gap / 2, w - gap, keyH - gap));
                x += w;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var (pad, unit, keyH, barH) = Metrics();
        float size = Math.Min(keyH, unit * 1.25f); // text fits narrow keys too
        var keyFont = FontFor(size * 0.36f);
        var smallFont = FontFor(size * 0.2f);
        var barFont = FontFor(barH * 0.5f);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var topRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Near };
        using var bottomRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
        using var leftMiddle = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var hintBrush = new SolidBrush(HintColor);
        using var textBrush = new SolidBrush(TextColor);
        using var accentBrush = new SolidBrush(Accent);

        lock (_model.Sync)
        {
            // Top bar: hints, drag area, ✕.
            using (var barBrush = new SolidBrush(BarColor)) g.FillRectangle(barBrush, BarRect());
            var close = CloseRect();
            var hintRect = new RectangleF(pad * 2, pad / 2, close.Left - pad * 3, barH);
            g.DrawString(_model.Hints(), barFont, _model.Editing ? accentBrush : hintBrush, hintRect, leftMiddle);
            g.DrawString("✕", barFont, textBrush, close, centered);
            if (_model.Editing)
                using (var pen = new Pen(Accent, Math.Max(2f, pad * 0.4f))) g.DrawRectangle(pen, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);

            foreach (var (row, col, key, rect) in KeyRects())
            {
                bool highlighted = row == _model.Row && col == _model.Col;
                bool latched = _model.IsLatched(key);
                Color fill = highlighted ? Accent : key.Action == OskAction.Char ? KeyColor : SpecialKeyColor;

                using var path = RoundedRect(rect, Math.Min(rect.Height, rect.Width) * 0.14f);
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (latched && !highlighted)
                    using (var pen = new Pen(Accent, Math.Max(2f, rect.Height * 0.05f))) g.DrawPath(pen, path);

                var labelBrush = latched && !highlighted ? accentBrush : _model.IsDimmed(key) && !highlighted ? hintBrush : textBrush;
                g.DrawString(_model.LabelOf(key), keyFont, labelBrush, rect, centered);

                var inner = RectangleF.Inflate(rect, -rect.Height * 0.08f, -rect.Height * 0.04f);
                var subBrush = highlighted ? textBrush : hintBrush;
                string corner = _model.CornerOf(key);
                if (corner != "") g.DrawString(corner, smallFont, subBrush, inner, topRight);
                string badge = _model.BadgeOf(key);
                if (badge != "") g.DrawString(badge, smallFont, subBrush, inner, bottomRight);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (BarRect().Contains(e.Location))
        {
            if (CloseRect().Contains(e.Location)) KeyboardHost.Close();
            else if (e.Button == MouseButtons.Left) { _dragOffset = e.Location; Capture = true; }
            return;
        }

        int row = -1, col = -1;
        lock (_model.Sync)
        {
            foreach (var (r, c, _, rect) in KeyRects())
                if (rect.Contains(e.Location)) { row = r; col = c; break; }
        }
        if (row >= 0) _model.ActivateAt(row, col, hold: e.Button == MouseButtons.Right);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOffset is not { } offset) return;
        var p = PointToScreen(e.Location);
        Location = new Point(p.X - offset.X, p.Y - offset.Y);
        lock (_model.Sync) if (_model.Editing) _layoutDirty = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragOffset == null) return;
        _dragOffset = null;
        Capture = false;
        bool editing;
        lock (_model.Sync) editing = _model.Editing;
        if (editing) SaveLayout(); // like the right stick: only saved in edit mode
    }

    private Font FontFor(float pixels)
    {
        int key = Math.Max(6, (int)pixels);
        if (!_fonts.TryGetValue(key, out var font))
            _fonts[key] = font = new Font("Segoe UI", key, FontStyle.Regular, GraphicsUnit.Pixel);
        return font;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stickTimer.Dispose();
            foreach (var f in _fonts.Values) f.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
