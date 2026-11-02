using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WhisperShortcut;

/// <summary>
/// The notice that used to be a Windows balloon: a dark card in the bottom right corner with an icon,
/// a title and up to a few lines of text. Fades in, stays a few seconds, closes on a click (which also
/// runs the notice's action, e.g. opening the settings). Never takes focus, so a paste in the user's
/// window is not disturbed. One notice at a time: a new one replaces the one on screen.
/// </summary>
internal sealed class Toast : Form
{
    private static readonly TimeSpan Shown = TimeSpan.FromSeconds(7);
    private const float ShadowMargin = 24, CardW = 384, Pad = 18, IconCol = 44, FadeIn = 0.18f, FadeOut = 0.2f;
    private const int MaxLines = 5;

    private static readonly string BodyFace =
        FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly StringFormat _line = new(StringFormat.GenericTypographic)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
    };
    private readonly StringFormat _wrap = new(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
    private Font? _titleFont, _bodyFont;
    private int _fontDpi;

    private string _title = "", _text = "";
    private ToolTipIcon _kind;
    private Action? _onClick;
    private DateTime _appearStart, _until;
    private DateTime? _fadeStart;
    private Point _basePos;

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AccessibleRole = AccessibleRole.Alert;
        Cursor = Cursors.Hand;
        _timer.Tick += (_, _) => OnTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private float S => DeviceDpi / 96f;
    private static bool Hc => SystemInformation.HighContrast;

    /// <summary>Shows the notice; <paramref name="onClick"/> runs when the user clicks it.</summary>
    public void Present(string title, string text, ToolTipIcon kind, Action? onClick)
    {
        _title = title;
        _text = text;
        _kind = kind;
        _onClick = onClick;
        _fadeStart = null;
        _until = DateTime.UtcNow + Shown;
        EnsureFonts();

        using var probe = LayeredWindow.NewBitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        Configure(pg);
        float s = S;
        int m = (int)Math.Ceiling(ShadowMargin * s);
        var card = CardSize(pg);
        var size = new Size((int)Math.Ceiling(card.Width) + 2 * m, (int)Math.Ceiling(card.Height) + 2 * m);
        var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
        _basePos = new Point(area.Right - size.Width + m / 2 - (int)(8 * s), area.Bottom - size.Height + m / 2 - (int)(8 * s));
        if (Size != size || Location != _basePos) SetBounds(_basePos.X, _basePos.Y, size.Width, size.Height);
        AccessibleName = title;
        AccessibleDescription = text;
        if (!Visible)
        {
            _appearStart = DateTime.UtcNow;
            Show();
        }
        _timer.Start();
        Redraw();
    }

    public void Dismiss()
    {
        _timer.Stop();
        _fadeStart = null;
        if (Visible) Hide();
    }

    private void EnsureFonts()
    {
        if (_titleFont != null && _fontDpi == DeviceDpi) return;
        _titleFont?.Dispose();
        _bodyFont?.Dispose();
        _fontDpi = DeviceDpi;
        _titleFont = new Font("Segoe UI Semibold", 14.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font(BodyFace, 12.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static void Configure(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private float BodyWidth => (CardW - Pad - IconCol - Pad) * S;

    private float BodyHeight(Graphics g)
    {
        float line = _bodyFont!.GetHeight(g);
        float h = g.MeasureString(_text, _bodyFont, new SizeF(BodyWidth, 2000), _wrap).Height;
        return Math.Min(h, line * MaxLines);
    }

    private SizeF CardSize(Graphics g)
    {
        float s = S;
        return new SizeF(CardW * s, (Pad + 20) * s + 4 * s + BodyHeight(g) + Pad * s);
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        if (_fadeStart == null && now >= _until) _fadeStart = now;
        if (_fadeStart != null && (now - _fadeStart.Value).TotalSeconds >= FadeOut)
        {
            Dismiss();
            return;
        }
        Redraw();
    }

    private static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0f, 1f), 3);

    private void Redraw()
    {
        if (!IsHandleCreated || !Visible || Width <= 0 || Height <= 0) return;
        EnsureFonts();
        float s = S;
        var now = DateTime.UtcNow;
        float appear = Ease((float)(now - _appearStart).TotalSeconds / FadeIn);
        float fade = _fadeStart == null ? 1f : 1f - Ease((float)(now - _fadeStart.Value).TotalSeconds / FadeOut);

        using var bmp = LayeredWindow.NewBitmap(Width, Height);
        using (var g = Graphics.FromImage(bmp))
        {
            Configure(g);
            float m = ShadowMargin * s;
            Paint(g, new RectangleF(m, m, Width - 2 * m, Height - 2 * m), s);
        }
        // Slides in from the right edge.
        int slide = (int)((1 - appear) * 18 * s);
        LayeredWindow.Update(Handle, bmp, new Point(_basePos.X + slide, _basePos.Y), (byte)Math.Clamp(255 * appear * fade, 0, 255));
    }

    private Color Tone => Hc ? SystemColors.WindowText : _kind switch
    {
        ToolTipIcon.Error => Color.FromArgb(255, 99, 90),
        ToolTipIcon.Warning => Color.FromArgb(255, 189, 70),
        _ => Color.FromArgb(96, 175, 255),
    };

    private void Paint(Graphics g, RectangleF card, float s)
    {
        float radius = 16 * s;
        if (!Hc) LayeredWindow.Shadow(g, card, radius, s);
        using (var path = Theme.Rounded(card, radius))
        {
            if (Hc)
            {
                using var fill = new SolidBrush(SystemColors.Window);
                g.FillPath(fill, path);
                using var edge = new Pen(SystemColors.WindowFrame, 1.5f);
                g.DrawPath(edge, path);
            }
            else
            {
                using var fill = new LinearGradientBrush(card, Color.FromArgb(255, 42, 42, 49), Color.FromArgb(255, 25, 25, 30), 90f);
                g.FillPath(fill, path);
                using var inner = Theme.Rounded(RectangleF.Inflate(card, -0.5f, -0.5f), radius - 0.5f);
                using var rim = new LinearGradientBrush(card, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(22, 255, 255, 255), 90f);
                using var pen = new Pen(rim, 1f);
                g.DrawPath(pen, inner);
            }
        }

        var primary = Hc ? SystemColors.WindowText : Color.FromArgb(247, 247, 250);
        var secondary = Hc ? SystemColors.WindowText : Color.FromArgb(200, 200, 208);
        float x = card.X + Pad * s, y = card.Y + Pad * s;
        DrawIcon(g, new PointF(x + 12 * s, y + 10 * s), s);

        float textX = card.X + (Pad + IconCol) * s;
        using (var brush = new SolidBrush(primary))
            g.DrawString(_title, _titleFont!, brush, new RectangleF(textX, y, card.Right - textX - Pad * s, 22 * s), _line);
        using (var brush = new SolidBrush(secondary))
            g.DrawString(_text, _bodyFont!, brush, new RectangleF(textX, y + 24 * s, BodyWidth, _bodyFont!.GetHeight(g) * MaxLines), _wrap);
    }

    private void DrawIcon(Graphics g, PointF c, float s)
    {
        float r = 11f * s;
        using var disc = new SolidBrush(Tone);
        g.FillEllipse(disc, c.X - r, c.Y - r, r * 2, r * 2);
        var ink = Hc ? SystemColors.Window : Color.FromArgb(24, 24, 29);
        using var mark = new Pen(ink, 2.1f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(ink);
        float d = 1.3f * s;
        switch (_kind)
        {
            case ToolTipIcon.Error:
                g.DrawLine(mark, c.X - 3.6f * s, c.Y - 3.6f * s, c.X + 3.6f * s, c.Y + 3.6f * s);
                g.DrawLine(mark, c.X + 3.6f * s, c.Y - 3.6f * s, c.X - 3.6f * s, c.Y + 3.6f * s);
                break;
            case ToolTipIcon.Warning:
                g.DrawLine(mark, c.X, c.Y - 5f * s, c.X, c.Y + 1.2f * s);
                g.FillEllipse(dot, c.X - d, c.Y + 4.4f * s - d, d * 2, d * 2);
                break;
            default:
                g.DrawLine(mark, c.X, c.Y - 0.8f * s, c.X, c.Y + 4.6f * s);
                g.FillEllipse(dot, c.X - d, c.Y - 4.8f * s - d, d * 2, d * 2);
                break;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var action = _onClick;
        Dismiss();
        action?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
            _line.Dispose();
            _wrap.Dispose();
        }
        base.Dispose(disposing);
    }
}
