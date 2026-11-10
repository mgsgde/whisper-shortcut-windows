using System.Drawing.Drawing2D;

namespace WhisperShortcut;

/// <summary>
/// The Vorlesen card (Mac: the pill turns into the Read Aloud transport): while preparing a spinner and Stopp; while speaking
/// −10 s, Pause/Fortsetzen, +10 s, the speed, Stopp, and a progress bar that seeks on click. Bottom center above the
/// taskbar like the dictation pill, draggable, and it never takes focus (WS_EX_NOACTIVATE): the user keeps reading in
/// their app. Same colours as <see cref="MeetingOverlay"/>.
/// </summary>
internal sealed class ReadAloudOverlay : OverlayForm
{
    private enum Phase { Hidden, Preparing, Speaking }

    /// <summary>The buttons, left to right in the speaking card.</summary>
    public enum Command { Back, PlayPause, Forward, Speed, Stop }

    private static readonly Color Back = Color.FromArgb(30, 30, 30);
    private static readonly Color Border = Color.FromArgb(62, 62, 62);
    private static readonly Color Primary = Color.White;
    private static readonly Color Muted = Color.FromArgb(150, 150, 150);
    private static readonly Color Accent = Color.FromArgb(76, 194, 255);
    private static readonly Color Track = Color.FromArgb(70, 70, 70);
    private static readonly Color ButtonBack = Color.FromArgb(58, 58, 58);
    private static readonly Color ButtonHover = Color.FromArgb(80, 80, 80);

    // Segoe MDL2 Assets (Windows 10 and 11).
    private const string GlyphPlay = "\uE768", GlyphPause = "\uE769", GlyphStop = "\uE71A";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    private readonly Font _textFont = new("Segoe UI", 9f);
    private readonly Font _glyphFont = new("Segoe MDL2 Assets", 10f);

    private Phase _phase = Phase.Hidden;
    private Core.SpeechPlaybackBuffer? _buffer;
    private Func<bool> _isPaused = () => false;
    private int _tick;
    private readonly List<(Command Command, RectangleF Box)> _buttons = new();
    private RectangleF _bar;
    private Command? _hover;
    private Point? _dragFrom;
    private Point _dragOrigin;
    private Point? _userLocation;

    /// <summary>A button was clicked.</summary>
    public event Action<Command>? CommandClicked;

    /// <summary>The progress bar was clicked: seek to this share (0..1) of the duration.</summary>
    public event Action<double>? SeekClicked;

    public ReadAloudOverlay() : base(layered: false, clickThrough: false)
    {
        BackColor = Back;
        DoubleBuffered = true;
        Opacity = 0.97;
        AccessibleName = "Vorlesen";
        _timer.Tick += (_, _) => { _tick++; Invalidate(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedBorder(Border);
    }

    // ---------------- API for ReadAloudController ----------------

    /// <summary>Selection read, waiting for the rewrite or the first audio („Vorlesen wird vorbereitet …“).</summary>
    public void ShowPreparing()
    {
        _phase = Phase.Preparing;
        _buffer = null;
        AccessibleDescription = "Vorlesen wird vorbereitet";
        Present();
    }

    /// <summary>Audio is playing from <paramref name="buffer"/>.</summary>
    public void ShowSpeaking(Core.SpeechPlaybackBuffer buffer, Func<bool> isPaused)
    {
        _phase = Phase.Speaking;
        _buffer = buffer;
        _isPaused = isPaused;
        AccessibleDescription = "Vorlesen läuft";
        Present();
    }

    public void HideOverlay()
    {
        _phase = Phase.Hidden;
        _buffer = null;
        _hover = null;
        _timer.Stop();
        Hide();
    }

    // ---------------- layout ----------------

    private void Present()
    {
        var size = new Size((int)(_phase == Phase.Speaking ? 400 * S : 320 * S), (int)(_phase == Phase.Speaking ? 70 * S : 48 * S));
        Point location;
        if (_userLocation is { } placed && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(placed, size))))
        {
            location = placed;
        }
        else
        {
            // Where the dictation pill sits: on the screen the user works on, centered above the taskbar.
            var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
            location = new Point(area.Left + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(16 * S));
        }
        if (Size != size || Location != location) SetBounds(location.X, location.Y, size.Width, size.Height);
        if (!Visible) Show();
        _timer.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = S;
        float pad = 14 * s;
        float row1 = _phase == Phase.Speaking ? 22 * s : Height / 2f;
        _buttons.Clear();

        // Right to left: Stopp always; while speaking also speed, +10, play/pause, −10.
        float right = Width - pad;
        right = AddButton(g, Command.Stop, GlyphStop, glyph: true, right, row1, s);
        if (_phase == Phase.Speaking && _buffer != null)
        {
            right = AddButton(g, Command.Speed, Core.ReadAloud.SpeedLabel(_buffer.Speed), glyph: false, right, row1, s);
            right = AddButton(g, Command.Forward, "+10 s", glyph: false, right, row1, s);
            right = AddButton(g, Command.PlayPause, _isPaused() ? GlyphPlay : GlyphPause, glyph: true, right, row1, s);
            right = AddButton(g, Command.Back, "−10 s", glyph: false, right, row1, s);
        }

        float textX = pad + 22 * s;
        DrawIndicator(g, new PointF(pad + 7 * s, row1), s);
        string title = _phase == Phase.Preparing ? "Vorlesen wird vorbereitet …" : "Vorlesen";
        var titleSize = TextRenderer.MeasureText(g, title, _titleFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, title, _titleFont, new Point((int)textX, (int)(row1 - titleSize.Height / 2f)), Primary, TextFormatFlags.NoPadding);

        if (_phase != Phase.Speaking || _buffer == null)
        {
            _bar = RectangleF.Empty;
            return;
        }

        // Time: position / length; „Lädt …“ while the next audio has not arrived yet (Mac: buffering indicator).
        double pos = _buffer.PositionSeconds, total = Math.Max(_buffer.DurationSeconds, pos);
        string time = _buffer.IsStarved ? "Lädt …" : _isPaused() ? $"Pausiert · {Format(pos)}" : $"{Format(pos)} / {Format(total)}";
        float timeX = textX + titleSize.Width + 10 * s;
        var timeSize = TextRenderer.MeasureText(g, time, _textFont, Size.Empty, TextFormatFlags.NoPadding);
        if (timeX + timeSize.Width < right - 4 * s)
            TextRenderer.DrawText(g, time, _textFont, new Point((int)timeX, (int)(row1 - timeSize.Height / 2f)), Muted, TextFormatFlags.NoPadding);

        // Progress bar under the row, full width; light part = audio received so far.
        float barY = 50 * s, barH = 4 * s;
        _bar = new RectangleF(pad, barY - 6 * s, Width - 2 * pad, barH + 12 * s); // larger hit area than the line
        var line = new RectangleF(pad, barY, Width - 2 * pad, barH);
        using (var track = new SolidBrush(Track))
        using (var path = Theme.Rounded(line, barH / 2))
            g.FillPath(track, path);
        if (total > 0)
        {
            float buffered = (float)Math.Clamp(_buffer.BufferedSeconds / total, 0, 1);
            float played = (float)Math.Clamp(pos / total, 0, 1);
            if (buffered > 0)
            {
                using var light = new SolidBrush(Color.FromArgb(110, 110, 110));
                using var path = Theme.Rounded(new RectangleF(line.X, line.Y, Math.Max(barH, line.Width * buffered), barH), barH / 2);
                g.FillPath(light, path);
            }
            if (played > 0)
            {
                using var fill = new SolidBrush(Accent);
                using var path = Theme.Rounded(new RectangleF(line.X, line.Y, Math.Max(barH, line.Width * played), barH), barH / 2);
                g.FillPath(fill, path);
            }
        }
    }

    private float AddButton(Graphics g, Command command, string label, bool glyph, float right, float centerY, float s)
    {
        var font = glyph ? _glyphFont : _textFont;
        var size = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPadding);
        float bh = 26 * s, bw = Math.Max(bh, size.Width + 14 * s);
        var box = new RectangleF(right - bw, centerY - bh / 2, bw, bh);
        using (var fill = new SolidBrush(_hover == command ? ButtonHover : ButtonBack))
        using (var path = Theme.Rounded(box, 6 * s))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, label, font,
            new Point((int)(box.X + (bw - size.Width) / 2f), (int)(box.Y + (bh - size.Height) / 2f)), Primary, TextFormatFlags.NoPadding);
        _buttons.Add((command, box));
        return box.X - 6 * s;
    }

    private void DrawIndicator(Graphics g, PointF c, float s)
    {
        if (_phase == Phase.Preparing || _buffer?.IsStarved == true)
        {
            float r = 6.5f * s;
            using var track = new Pen(Color.FromArgb(70, Accent), 2.2f * s);
            using var arc = new Pen(Accent, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var box = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawEllipse(track, box);
            g.DrawArc(arc, box, _tick * 36 % 360, 100);
            return;
        }
        // Three bars that move while speaking and stand still while paused.
        using var brush = new SolidBrush(Accent);
        float barW = 2.4f * s, gap = 2.2f * s;
        for (int i = 0; i < 3; i++)
        {
            float h = _isPaused() ? 5 * s : (5 + 7 * (0.5f + 0.5f * MathF.Sin(_tick * 0.9f + i * 1.9f))) * s;
            g.FillRectangle(brush, c.X - 1.5f * barW - gap + i * (barW + gap), c.Y - h / 2, barW, h);
        }
    }

    // ---------------- mouse: buttons, seeking, dragging ----------------

    private Command? HitButton(Point p) => _buttons.FirstOrDefault(b => b.Box.Contains(p)) is { Box.Width: > 0 } hit ? hit.Command : null;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || HitButton(e.Location) != null || _bar.Contains(e.Location)) return;
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
        var hover = HitButton(e.Location);
        Cursor = hover != null || _bar.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
        if (hover == _hover) return;
        _hover = hover;
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
        if (_hover == null) return;
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _phase == Phase.Hidden) return;
        if (HitButton(e.Location) is { } command)
        {
            CommandClicked?.Invoke(command);
            Invalidate();
        }
        else if (_bar.Width > 0 && _bar.Contains(e.Location))
        {
            SeekClicked?.Invoke(Math.Clamp((e.X - _bar.X) / _bar.Width, 0, 1));
            Invalidate();
        }
    }

    private static string Format(double seconds) => Core.Clock.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
            _glyphFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
