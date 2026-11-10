using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WhisperShortcut;

/// <summary>
/// Notices like the Mac popups (PopupNotificationWindow, position "Center bottom"): a dark card at the bottom centre
/// of the work area, above the recording overlay while that is visible, with an icon, a title, up to a few lines of
/// text, a visible action button when the notice offers one, and ✕ to close. Info stays 3 s, warnings and errors 30 s,
/// an error with an action stays until it is used or closed. A click elsewhere on the card closes it. Never takes
/// focus, so a paste in the user's window is not disturbed. One notice at a time: a new one replaces the one on screen.
/// </summary>
internal sealed class Toast : Form
{
    /// <summary>Mac notificationDuration: success and info.</summary>
    public static readonly TimeSpan InfoShown = TimeSpan.FromSeconds(3);

    /// <summary>Mac errorNotificationDuration: errors and warnings.</summary>
    public static readonly TimeSpan ErrorShown = TimeSpan.FromSeconds(30);

    private const float ShadowMargin = 24, CardW = 420, Pad = 18, IconCol = 44, FadeIn = 0.18f, FadeOut = 0.2f;
    private const float BottomMargin = 30, OverlayGap = 12, ButtonH = 30, ButtonGap = 12, CloseSize = 24;
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
    private Font? _titleFont, _bodyFont, _buttonFont;
    private int _fontDpi;

    private string _title = "", _text = "";
    private string? _actionLabel;
    private ToolTipIcon _kind;
    private Action? _onAction;
    private DateTime _appearStart;
    private DateTime? _until, _fadeStart;
    private Point _basePos;
    // Hit areas in client coordinates, set while painting.
    private RectangleF _actionRect, _closeRect;
    private enum Hot { None, Action, Close }
    private Hot _hot;
    // The last frame drawn showed the card at rest: until it fades, nothing on it moves (hover redraws itself).
    private bool _settled;

    /// <summary>Screen bounds of the recording overlay while it is visible (the notice then sits above it), else null.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Rectangle?>? AvoidBounds { get; set; }

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // WHY no TopMost = true: WinForms then activates the window on Show despite ShowWithoutActivation, which
        // took the focus from the chat (it closes on focus loss) and from the target window. WS_EX_TOPMOST below keeps it on top.
        AccessibleRole = AccessibleRole.Alert;
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

    /// <summary>The balloon icon of a <see cref="Core.Severity"/> (Core decides, the toast draws).</summary>
    public static ToolTipIcon IconOf(Core.Severity severity) =>
        severity switch { Core.Severity.Error => ToolTipIcon.Error, Core.Severity.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info };

    /// <summary>How long a notice stays: null = until it is used or closed (an error with an action, like the Mac).</summary>
    public static TimeSpan? Lifetime(ToolTipIcon kind, bool hasAction) => kind switch
    {
        ToolTipIcon.Error when hasAction => null,
        ToolTipIcon.Error or ToolTipIcon.Warning => ErrorShown,
        _ => InfoShown,
    };

    /// <summary>
    /// Shows the notice. With <paramref name="onAction"/> a button labelled <paramref name="actionLabel"/> runs it
    /// (and closes the notice).
    /// </summary>
    public void Present(string title, string text, ToolTipIcon kind, Action? onAction, string? actionLabel)
    {
        _title = title;
        _text = text;
        _kind = kind;
        _onAction = onAction;
        _actionLabel = onAction == null ? null : string.IsNullOrWhiteSpace(actionLabel) ? "Öffnen" : actionLabel;
        _fadeStart = null;
        _hot = Hot.None;
        var life = Lifetime(kind, onAction != null);
        _until = life == null ? null : DateTime.UtcNow + life.Value;
        EnsureFonts();

        using var probe = LayeredWindow.NewBitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        Configure(pg);
        float s = S;
        int m = (int)Math.Ceiling(ShadowMargin * s);
        var card = CardSize(pg);
        var size = new Size((int)Math.Ceiling(card.Width) + 2 * m, (int)Math.Ceiling(card.Height) + 2 * m);
        var screen = Screen.FromHandle(NativeMethods.GetForegroundWindow());
        var area = screen.WorkingArea;
        // Bottom centre (Mac "Center bottom"), above the recording overlay when it is on this screen.
        int cardBottom = area.Bottom - (int)(BottomMargin * s);
        if (AvoidBounds?.Invoke() is { } overlay && area.IntersectsWith(overlay))
            cardBottom = Math.Min(cardBottom, overlay.Top - (int)(OverlayGap * s));
        _basePos = new Point(area.Left + (area.Width - size.Width) / 2, cardBottom - (int)Math.Ceiling(card.Height) - m);
        if (Size != size || Location != _basePos) SetBounds(_basePos.X, _basePos.Y, size.Width, size.Height);
        AccessibleName = title;
        AccessibleDescription = _actionLabel == null ? text : $"{text} ({_actionLabel})";
        if (!Visible)
        {
            _appearStart = DateTime.UtcNow;
            Show();
        }
        _settled = false;
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
        _buttonFont?.Dispose();
        _fontDpi = DeviceDpi;
        _titleFont = new Font("Segoe UI Semibold", 14.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font(BodyFace, 12.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        _buttonFont = new Font("Segoe UI Semibold", 12.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static void Configure(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    // The text column leaves room for ✕ on the right.
    private float BodyWidth => (CardW - Pad - IconCol - Pad - CloseSize / 2) * S;

    private float BodyHeight(Graphics g)
    {
        float line = _bodyFont!.GetHeight(g);
        float h = g.MeasureString(_text, _bodyFont, new SizeF(BodyWidth, 2000), _wrap).Height;
        return Math.Min(h, line * MaxLines);
    }

    private SizeF CardSize(Graphics g)
    {
        float s = S;
        float buttons = _actionLabel == null ? 0 : (ButtonGap + ButtonH) * s;
        return new SizeF(CardW * s, (Pad + 20) * s + 4 * s + BodyHeight(g) + buttons + Pad * s);
    }

    /// <summary>For the UI tests: one animation frame, as the 33 ms timer runs it.</summary>
    internal void TickForTests() => OnTick();

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        if (_fadeStart == null && _until is { } until && now >= until) _fadeStart = now;
        if (_fadeStart != null && (now - _fadeStart.Value).TotalSeconds >= FadeOut)
        {
            Dismiss();
            return;
        }
        // WHY: a warning stays 30 s and an error with an action until it is used; repainting the card (shadow, text)
        // 30 times a second for that long only took CPU from the transcription. One frame at rest, then until the fade.
        bool animating = _fadeStart != null || (now - _appearStart).TotalSeconds < FadeIn;
        if (!animating && _settled) return;
        _settled = !animating;
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
            PaintCard(g, new RectangleF(m, m, Width - 2 * m, Height - 2 * m), s);
        }
        // Rises from below, like the Mac popup.
        int slide = (int)((1 - appear) * 14 * s);
        LayeredWindow.Update(Handle, bmp, new Point(_basePos.X, _basePos.Y + slide), (byte)Math.Clamp(255 * appear * fade, 0, 255));
    }

    private Color Tone => Hc ? SystemColors.WindowText : _kind switch
    {
        ToolTipIcon.Error => Color.FromArgb(255, 99, 90),
        ToolTipIcon.Warning => Color.FromArgb(255, 189, 70),
        _ => Color.FromArgb(96, 175, 255),
    };

    private void PaintCard(Graphics g, RectangleF card, float s)
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
            g.DrawString(_title, _titleFont!, brush, new RectangleF(textX, y, card.Right - textX - (Pad + CloseSize) * s, 22 * s), _line);
        float bodyH = BodyHeight(g);
        using (var brush = new SolidBrush(secondary))
            g.DrawString(_text, _bodyFont!, brush, new RectangleF(textX, y + 24 * s, BodyWidth, bodyH + 2 * s), _wrap);

        // ✕ in the top right corner.
        _closeRect = new RectangleF(card.Right - (Pad / 2 + CloseSize) * s, card.Y + Pad / 2 * s, CloseSize * s, CloseSize * s);
        if (_hot == Hot.Close && !Hc)
        {
            using var hover = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
            g.FillEllipse(hover, _closeRect);
        }
        using (var cross = new Pen(_hot == Hot.Close ? primary : secondary, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = _closeRect.X + _closeRect.Width / 2, cy = _closeRect.Y + _closeRect.Height / 2, k = 4.2f * s;
            g.DrawLine(cross, cx - k, cy - k, cx + k, cy + k);
            g.DrawLine(cross, cx + k, cy - k, cx - k, cy + k);
        }

        // The action as a real button under the text (Mac: Retry, Top up, Sign in …).
        _actionRect = RectangleF.Empty;
        if (_actionLabel != null)
        {
            var labelSize = g.MeasureString(_actionLabel, _buttonFont!, PointF.Empty, _line);
            float w = Math.Min(labelSize.Width + 28 * s, card.Right - textX - Pad * s);
            _actionRect = new RectangleF(textX, y + 24 * s + bodyH + ButtonGap * s, w, ButtonH * s);
            using var path = Theme.Rounded(_actionRect, 6 * s);
            var fill = Hc ? SystemColors.Highlight : _hot == Hot.Action ? Color.FromArgb(120, 190, 255) : Color.FromArgb(96, 175, 255);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using var ink = new SolidBrush(Hc ? SystemColors.HighlightText : Color.FromArgb(16, 18, 24));
            using var centred = new StringFormat(_line) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_actionLabel, _buttonFont!, ink, _actionRect, centred);
        }
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

    private Hot HitTest(Point p) =>
        _closeRect.Contains(p) ? Hot.Close : _actionRect.Contains(p) ? Hot.Action : Hot.None;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = HitTest(e.Location);
        Cursor = hot == Hot.None ? Cursors.Default : Cursors.Hand;
        if (hot == _hot) return;
        _hot = hot;
        Redraw();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot == Hot.None) return;
        _hot = Hot.None;
        Redraw();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // The button runs the action; ✕ and any other spot just close (Mac: a click on the popup hides it).
        var action = HitTest(e.Location) == Hot.Action ? _onAction : null;
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
            _buttonFont?.Dispose();
            _line.Dispose();
            _wrap.Dispose();
        }
        base.Dispose(disposing);
    }
}
