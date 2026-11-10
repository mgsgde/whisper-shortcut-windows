using System.Drawing.Drawing2D;

namespace WhisperShortcut;

/// <summary>
/// The small card that stays on screen while a meeting records: pulsing dot, elapsed time, what is
/// recorded (microphone alone or with the PC's sound), how far the transcript is, and a "Beenden"
/// button. Same look as <see cref="RecordingOverlay"/>, but in the bottom right corner above the
/// tray, so it never covers the dictation pill (a dictation can run during a meeting) and can be
/// dragged elsewhere when it is in the way of a shared screen.
/// It never takes focus (WS_EX_NOACTIVATE, MA_NOACTIVATE): the user is typing into the meeting app.
/// </summary>
internal sealed class MeetingOverlay : OverlayForm
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
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly float[] _levels = new float[Bars];
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    private readonly Font _textFont = new("Segoe UI", 9f);

    private Phase _phase = Phase.Hidden;
    private DateTime _started;
    private Func<float>? _level;
    private readonly Core.LevelMeter _meter = new(0.8f);
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

    public MeetingOverlay() : base(layered: false, clickThrough: false)
    {
        BackColor = Back;
        DoubleBuffered = true;
        Opacity = 0.97;
        AccessibleName = "Meeting-Aufnahme";
        _timer.Tick += (_, _) => OnTick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedBorder(Border);
    }

    // ---------------- API for MeetingController ----------------

    /// <param name="startedUtc">Start of the meeting: the time shown counts from here.</param>
    /// <param name="level">Live level 0..1 of what is recorded; null in the test mode without audio.</param>
    public void ShowRecording(DateTime startedUtc, Func<float>? level)
    {
        _phase = Phase.Recording;
        _started = startedUtc;
        _level = level;
        _meter.Reset();
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
            float smoothed = _meter.Feed(_level());
            // Bars move every 100 ms: a quieter meter than the dictation pill, it stays on screen for an hour.
            if (_tick % 2 == 0)
            {
                _levels[_levelHead] = smoothed;
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
            using (var path = Theme.Rounded(_button, 6 * s))
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
            string time = Core.Clock.Format(DateTime.UtcNow - _started);
            var timeSize = TextRenderer.MeasureText(g, time, _textFont, Size.Empty, TextFormatFlags.NoPadding);
            float timeX = textX + titleSize.Width + 10 * s;
            TextRenderer.DrawText(g, time, _textFont, new Point((int)timeX, (int)(row1 - timeSize.Height / 2f)), Secondary, TextFormatFlags.NoPadding);
            float barsRight = right;
            float barsLeft = barsRight - Bars * 4 * s;
            if (_level != null && barsLeft > timeX + timeSize.Width + 8 * s) DrawBars(g, barsRight, row1, s);
        }

        string status = _status;
        Color color = _statusWarn ? Amber : Muted;
        if (_phase == Phase.Recording && _level != null && Core.LevelMeter.IsSilent(_meter.Peak, DateTime.UtcNow - _started, SilentAfter))
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
}
