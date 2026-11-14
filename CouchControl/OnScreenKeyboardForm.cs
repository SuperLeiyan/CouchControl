using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CouchControl;

/// <summary>
/// Draws <see cref="OnScreenKeyboardModel"/>. A topmost window that never takes focus, so keys
/// go to the app you're typing in. Mouse clicks on keys work too.
/// </summary>
internal sealed class OnScreenKeyboardForm : Form
{
    private readonly OnScreenKeyboardModel _model;
    private readonly Dictionary<int, Font> _fonts = new();

    private static readonly Color Background = Color.FromArgb(28, 28, 28);
    private static readonly Color KeyColor = Color.FromArgb(58, 58, 58);
    private static readonly Color SpecialKeyColor = Color.FromArgb(44, 44, 44);
    private static readonly Color Accent = Color.FromArgb(0, 120, 215);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
    private static readonly Color HintColor = Color.FromArgb(160, 160, 160);

    private const float PadRatio = 0.012f, KeyHeightRatio = 0.82f, HintRatio = 0.55f;

    public OnScreenKeyboardForm(OnScreenKeyboardModel model)
    {
        _model = model;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Opacity = 0.96;
        Text = "CouchControl Keyboard";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _model.Changed += () => { if (IsHandleCreated) BeginInvoke(Invalidate); };
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

    /// <summary>Sizes and docks the keyboard on the monitor of the window being typed in.</summary>
    public void PlaceOnScreen()
    {
        IntPtr fg = GetForegroundWindow();
        var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen!;
        var wa = screen.WorkingArea;

        int width = Math.Min((int)(wa.Width * 0.6), 1600);
        int height = HeightFor(width);
        if (height > wa.Height * 0.45)
        {
            width = (int)(width * wa.Height * 0.45 / height);
            height = HeightFor(width);
        }

        bool top;
        lock (_model.Sync) top = _model.DockTop;
        int margin = (int)(wa.Height * 0.015);
        Bounds = new Rectangle(wa.Left + (wa.Width - width) / 2,
            top ? wa.Top + margin : wa.Bottom - height - margin, width, height);
        Invalidate();
    }

    private static int HeightFor(int width)
    {
        float pad = width * PadRatio;
        float unit = (width - 2 * pad) / (float)OnScreenKeyboardModel.RowUnits;
        float keyH = unit * KeyHeightRatio;
        int rows = OnScreenKeyboardModel.Pages[0].Length;
        return (int)(2 * pad + rows * keyH + keyH * HintRatio);
    }

    private (float pad, float unit, float keyH) Metrics()
    {
        float pad = ClientSize.Width * PadRatio;
        float unit = (ClientSize.Width - 2 * pad) / (float)OnScreenKeyboardModel.RowUnits;
        return (pad, unit, unit * KeyHeightRatio);
    }

    /// <summary>Key rectangles of the current page (call under the model lock).</summary>
    private IEnumerable<(int row, int col, OskKey key, RectangleF rect)> KeyRects()
    {
        var (pad, unit, keyH) = Metrics();
        float gap = unit * 0.08f;
        var page = OnScreenKeyboardModel.Pages[_model.Page];
        for (int r = 0; r < page.Length; r++)
        {
            float x = pad;
            for (int c = 0; c < page[r].Length; c++)
            {
                var k = page[r][c];
                float w = (float)k.Width * unit;
                yield return (r, c, k, new RectangleF(x + gap / 2, pad + r * keyH + gap / 2, w - gap, keyH - gap));
                x += w;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var (pad, _, keyH) = Metrics();
        var keyFont = FontFor(keyH * 0.36f);
        var hintFont = FontFor(keyH * 0.24f);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        lock (_model.Sync)
        {
            foreach (var (row, col, key, rect) in KeyRects())
            {
                bool highlighted = row == _model.Row && col == _model.Col;
                bool modifierOn = (key.Action == OskAction.Shift && _model.Shift != OnScreenKeyboardModel.ShiftMode.Off)
                    || (key.Action == OskAction.Ctrl && _model.Ctrl);
                Color fill = highlighted ? Accent : key.Action == OskAction.Char ? KeyColor : SpecialKeyColor;

                using var path = RoundedRect(rect, rect.Height * 0.14f);
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (modifierOn && !highlighted)
                    using (var pen = new Pen(Accent, Math.Max(2f, rect.Height * 0.05f))) g.DrawPath(pen, path);

                string label = key.Action == OskAction.Shift && _model.Shift == OnScreenKeyboardModel.ShiftMode.Caps
                    ? "⇪" : _model.LabelOf(key);
                using var textBrush = new SolidBrush(modifierOn && !highlighted ? Accent : TextColor);
                g.DrawString(label, keyFont, textBrush, rect, centered);
            }

            int rows = OnScreenKeyboardModel.Pages[_model.Page].Length;
            var hintRect = new RectangleF(pad, pad + rows * keyH, ClientSize.Width - 2 * pad, keyH * HintRatio);
            using var hintBrush = new SolidBrush(HintColor);
            g.DrawString(_model.Hints(), hintFont, hintBrush, hintRect, centered);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int row = -1, col = -1;
        lock (_model.Sync)
        {
            foreach (var (r, c, _, rect) in KeyRects())
                if (rect.Contains(e.Location)) { row = r; col = c; break; }
        }
        if (row >= 0) _model.ActivateAt(row, col);
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
            foreach (var f in _fonts.Values) f.Dispose();
        base.Dispose(disposing);
    }

    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
