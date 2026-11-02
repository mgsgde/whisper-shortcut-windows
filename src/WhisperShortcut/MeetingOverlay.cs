using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// The small card that stays on screen while a meeting records: pulsing dot, elapsed time, what is
/// recorded (microphone alone or with the PC's sound), how far the transcript is, and a "Beenden"
/// button. Same look as <see cref="RecordingOverlay"/>, but in the bottom right corner above the
/// tray, so it never covers the dictation pill (a dictation can run during a meeting) and can be
/// dragged elsewhere when it is in the way of a shared screen.
/// It never takes focus (WS_EX_NOACTIVATE, MA_NOACTIVATE): the user is typing into the meeting app.
/// </summary>
internal sealed class MeetingOverlay : Form
{
    private enum Phase { Hidden, Recording, Finishing }

    private static readonly Color Back = Color.FromArgb(30, 30, 30);
    private static readonly Color Border = Color.FromArgb(62, 62, 62);
    private static readonly Color Primary = Color.White;
    private static readonly Color Secondary = Color.FromArgb(205, 205, 205);
    private static readonly Color Muted = Color.FromArgb(150, 150, 150);
    private static readonly Color Red = Color.FromArgb(255, 69, 58);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color ButtonBack = Color.FromArgb(58, 58, 58);
    private static readonly Color ButtonHover = Color.FromArgb(80, 80, 80);

    private const int Bars = 12;
    // Under 1 % peak after 5 s: a muted or blocked microphone, not a quiet room (same bar as the dictation pill).
    private const float SilentPeak = 0.01f;
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly float[] _levels = new float[Bars];
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    private readonly Font _textFont = new("Segoe UI", 9f);

    private Phase _phase = Phase.Hidden;
    private DateTime _started;
    private Func<float>? _level;
    private float _peakSoFar;
    private float _smoothed;
    private int _levelHead;
    private int _tick;
    private string _status = "";
    private bool _statusWarn;
    private RectangleF _button;
    private bool _buttonHover;
    // Dragging: where the mouse went down, and whether it moved far enough to be a drag rather than a click.
    private Point? _dragFrom;
    private Point _dragOrigin;
    private Point? _userLocation;

    /// <summary>The "Beenden" button was clicked.</summary>
    public event Action? StopClicked;

    public MeetingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Back;
        DoubleBuffered = true;
        Opacity = 0.97;
        AccessibleName = "Meeting-Aufnahme";
        _timer.Tick += (_, _) => OnTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND, Windows 11; Windows 10 ignores it
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(Border);
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
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

    // ---------------- API for MeetingController ----------------

    /// <param name="startedUtc">Start of the meeting: the time shown counts from here.</param>
    /// <param name="level">Live level 0..1 of what is recorded; null in the test mode without audio.</param>
    public void ShowRecording(DateTime startedUtc, Func<float>? level)
    {
        _phase = Phase.Recording;
        _started = startedUtc;
        _level = level;
        _peakSoFar = 0;
        _smoothed = 0;
        Array.Clear(_levels);
        Present();
    }

    /// <summary>Second line: "Mikrofon + PC-Ton · 4 Abschnitte" or, amber, a problem worth a look.</summary>
    public void SetStatus(string text, bool warn = false)
    {
        _status = text;
        _statusWarn = warn;
        if (_phase != Phase.Hidden) Invalidate();
    }

    /// <summary>Recording has stopped; the last piece is still being transcribed.</summary>
    public void ShowFinishing(string text)
    {
        _phase = Phase.Finishing;
        _status = text;
        _statusWarn = false;
        _buttonHover = false;
        Present();
    }

    public void HideOverlay()
    {
        _phase = Phase.Hidden;
        _timer.Stop();
        Hide();
    }

    // ---------------- layout ----------------

    private float S => DeviceDpi / 96f;

    private void Present()
    {
        var size = new Size((int)(320 * S), (int)(60 * S));
        Point location;
        if (_userLocation is { } placed && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(placed, size))))
        {
            location = placed;
        }
        else
        {
            // Bottom right of the main screen, just above the tray where the meeting was started.
            var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            location = new Point(area.Right - size.Width - (int)(16 * S), area.Bottom - size.Height - (int)(16 * S));
        }
        if (Size != size || Location != location) SetBounds(location.X, location.Y, size.Width, size.Height);
        if (!Visible) Show();
        _timer.Start();
        Invalidate();
    }

    private void OnTick()
    {
        _tick++;
        if (_phase == Phase.Recording && _level != null)
        {
            float raw = Math.Clamp(_level(), 0f, 1f);
            if (raw > _peakSoFar) _peakSoFar = raw;
            raw = MathF.Sqrt(raw);
            _smoothed = raw > _smoothed ? raw : _smoothed * 0.8f + raw * 0.2f;
            // Bars move every 100 ms: a quieter meter than the dictation pill, it stays on screen for an hour.
            if (_tick % 2 == 0)
            {
                _levels[_levelHead] = _smoothed;
                _levelHead = (_levelHead + 1) % Bars;
            }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = S;
        float pad = 16 * s;
        float row1 = 20 * s;
        float textX = pad + 20 * s;

        DrawIndicator(g, new PointF(pad + 6 * s, row1), s);

        // Right: the "Beenden" button while recording.
        float right = Width - pad;
        if (_phase == Phase.Recording)
        {
            const string label = "Beenden";
            var labelSize = TextRenderer.MeasureText(g, label, _textFont, Size.Empty, TextFormatFlags.NoPadding);
            float bw = labelSize.Width + 20 * s, bh = 24 * s;
            _button = new RectangleF(right - bw, (Height - bh) / 2f, bw, bh);
            using (var fill = new SolidBrush(_buttonHover ? ButtonHover : ButtonBack))
            using (var path = Rounded(_button, 6 * s))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, label, _textFont,
                new Point((int)(_button.X + (bw - labelSize.Width) / 2f), (int)(_button.Y + (bh - labelSize.Height) / 2f)),
                Primary, TextFormatFlags.NoPadding);
            right = _button.X - 12 * s;
        }
        else
        {
            _button = RectangleF.Empty;
        }

        string title = _phase == Phase.Finishing ? "Meeting wird abgeschlossen" : "Meeting läuft";
        var titleSize = TextRenderer.MeasureText(g, title, _titleFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, title, _titleFont, new Point((int)textX, (int)(row1 - titleSize.Height / 2f)), Primary, TextFormatFlags.NoPadding);

        if (_phase == Phase.Recording)
        {
            string time = Format(DateTime.UtcNow - _started);
            var timeSize = TextRenderer.MeasureText(g, time, _textFont, Size.Empty, TextFormatFlags.NoPadding);
            float timeX = textX + titleSize.Width + 10 * s;
            TextRenderer.DrawText(g, time, _textFont, new Point((int)timeX, (int)(row1 - timeSize.Height / 2f)), Secondary, TextFormatFlags.NoPadding);
            float barsRight = right;
            float barsLeft = barsRight - Bars * 4 * s;
            if (_level != null && barsLeft > timeX + timeSize.Width + 8 * s) DrawBars(g, barsRight, row1, s);
        }

        string status = _status;
        Color color = _statusWarn ? Amber : Muted;
        if (_phase == Phase.Recording && _level != null && DateTime.UtcNow - _started > SilentAfter && _peakSoFar < SilentPeak)
        {
            status = "Kein Ton. Mikrofon stumm oder gesperrt?";
            color = Amber;
        }
        TextRenderer.DrawText(g, status, _textFont, new Rectangle((int)textX, (int)(row1 + 12 * s), (int)(right - textX), (int)(18 * s)), color,
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    private void DrawIndicator(Graphics g, PointF c, float s)
    {
        if (_phase == Phase.Finishing)
        {
            float r = 6.5f * s;
            using var track = new Pen(Color.FromArgb(70, Amber), 2.2f * s);
            using var arc = new Pen(Amber, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var box = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawEllipse(track, box);
            g.DrawArc(arc, box, _tick * 18 % 360, 100);
            return;
        }
        float pulse = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(_tick * 0.12f));
        float rr = 5.5f * s;
        using var halo = new SolidBrush(Color.FromArgb((int)(70 * pulse), Red));
        g.FillEllipse(halo, c.X - rr * 1.8f, c.Y - rr * 1.8f, rr * 3.6f, rr * 3.6f);
        using var dot = new SolidBrush(Red);
        g.FillEllipse(dot, c.X - rr, c.Y - rr, rr * 2, rr * 2);
    }

    private void DrawBars(Graphics g, float rightEdge, float centerY, float s)
    {
        float barW = 2f * s, gap = 2f * s, maxH = 14f * s, minH = 2f * s;
        float x = rightEdge - Bars * (barW + gap);
        using var brush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        for (int i = 0; i < Bars; i++)
        {
            float v = _levels[(_levelHead + i) % Bars];
            float h = Math.Max(minH, v * maxH);
            g.FillRectangle(brush, x + i * (barW + gap), centerY - h / 2, barW, h);
        }
    }

    // ---------------- mouse: button and dragging ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _button.Contains(e.Location)) return;
        _dragFrom = Cursor.Position;
        _dragOrigin = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is { } from && e.Button == MouseButtons.Left)
        {
            var now = Cursor.Position;
            Location = new Point(_dragOrigin.X + now.X - from.X, _dragOrigin.Y + now.Y - from.Y);
            _userLocation = Location;
            return;
        }
        bool hover = _button.Contains(e.Location);
        Cursor = hover ? Cursors.Hand : Cursors.SizeAll;
        if (hover == _buttonHover) return;
        _buttonHover = hover;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragFrom = null;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_buttonHover) return;
        _buttonHover = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_phase == Phase.Recording && e.Button == MouseButtons.Left && _button.Contains(e.Location)) StopClicked?.Invoke();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
