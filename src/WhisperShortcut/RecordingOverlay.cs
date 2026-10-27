using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// The pill at the bottom of the screen, the Windows counterpart of the Mac's recording pill:
/// shows that a recording runs, that the microphone hears something, how long it runs, how to stop
/// or cancel it, and then "Wird verarbeitet …" and a short check mark when the text is in.
/// During Dictate Prompt it grows into a card with the quick instructions (Ctrl+1–5 or a click), and
/// after a language-model reply it can show the "Nicht in deinem Diktat" warning.
/// It never takes focus (WS_EX_NOACTIVATE, MA_NOACTIVATE), otherwise Ctrl+V would land in the pill
/// instead of the user's window. Clicks pass through (WS_EX_TRANSPARENT) except while the card or the
/// warning is up, which are meant to be clicked.
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private enum Phase { Hidden, Recording, Processing, Done, Warning }

    private static readonly Color Back = Color.FromArgb(30, 30, 30);
    private static readonly Color Border = Color.FromArgb(62, 62, 62);
    private static readonly Color Primary = Color.White;
    private static readonly Color Secondary = Color.FromArgb(205, 205, 205);
    private static readonly Color Muted = Color.FromArgb(150, 150, 150);
    private static readonly Color Red = Color.FromArgb(255, 69, 58);
    private static readonly Color Amber = Color.FromArgb(255, 176, 32);
    private static readonly Color Green = Color.FromArgb(50, 215, 75);

    private const int Bars = 24;
    // Under 1 % peak after 3 s: not a quiet speaker, a muted or blocked microphone (same bar as MicAudioSource).
    private const float SilentPeak = 0.01f;
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LimitWarning = TimeSpan.FromSeconds(60);
    // Long enough to read eight words and compare them with the note; a click closes it earlier.
    private static readonly TimeSpan WarningShown = TimeSpan.FromSeconds(20);
    private const int WarningMaxWords = 8;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly float[] _levels = new float[Bars];
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    private readonly Font _textFont = new("Segoe UI", 9f);

    private Phase _phase = Phase.Hidden;
    private string _title = "";
    private string _profileSuffix = "";  // " · <profile>" in the Dictate Prompt title, only with several profiles
    private string _hint = "";
    private string? _context;
    private bool _contextWarn;
    private Func<float>? _level;
    private Func<float>? _peakSoFar;
    private TimeSpan _limit;
    private DateTime _started;
    private DateTime _doneUntil;
    private bool _doneNeutral;
    private int _levelHead;
    private float _smoothed;
    private int _tick;
    private IReadOnlyList<string> _quick = Array.Empty<string>();
    private string _quickLabel = "";
    private readonly List<RectangleF> _rowRects = new();
    private int _hover = -1;
    private string _warningWords = "";
    private DateTime _warningUntil;

    /// <summary>A quick instruction row was clicked (index into the list given to ShowRecording).</summary>
    public event Action<int>? QuickActionClicked;

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Back;
        DoubleBuffered = true;
        // Layered, so WS_EX_TRANSPARENT really lets the mouse through to the window below.
        Opacity = 0.97;
        _timer.Tick += (_, _) => OnTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: rounded corners and a quiet border from DWM (smooth, unlike a Region). Windows 10
        // ignores both attributes and shows a square pill, which is fine.
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(Border);
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // A click on the card must not activate it: the user's window keeps focus for the paste.
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------- API for TrayApp ----------------

    /// <param name="level">Live level 0..1 (null for a test WAV: no bars, no silence warning).</param>
    /// <param name="quickActions">Dictate Prompt: the instructions offered as Ctrl+1–5 rows (empty: plain pill).</param>
    /// <param name="profileName">Dictate Prompt: the active profile, shown in the title (null: none, i.e. only one profile).</param>
    public void ShowRecording(Mode mode, string stopKey, TimeSpan limit, Func<float>? level, Func<float>? peakSoFar,
        IReadOnlyList<string>? quickActions = null, string? profileName = null)
    {
        _phase = Phase.Recording;
        _profileSuffix = mode == Mode.Prompt && !string.IsNullOrEmpty(profileName) ? $" · {profileName}" : "";
        _title = (mode == Mode.Dictate ? "Diktat läuft" : "Anweisung sprechen") + _profileSuffix;
        _hint = $"{stopKey} beendet · Esc bricht ab";
        // Dictate Prompt: what will be edited is known only after Ctrl+C (up to 0.5 s), see SetSelection.
        _context = mode == Mode.Prompt ? "Markierung wird gelesen …" : null;
        _quick = mode == Mode.Prompt ? quickActions ?? Array.Empty<string>() : Array.Empty<string>();
        _quickLabel = "Oder direkt anwenden:";
        _hover = -1;
        _contextWarn = false;
        _level = level;
        _peakSoFar = peakSoFar;
        _limit = limit;
        _started = DateTime.UtcNow;
        Array.Clear(_levels);
        _smoothed = 0;
        Present();
    }

    /// <summary>Dictate Prompt: the selection is known. null = nothing selected, new text is written.</summary>
    public void SetSelection(string? selection)
    {
        if (_phase != Phase.Recording || _context == null) return;
        if (selection == null)
        {
            // Nothing to edit: what the user says is the text (a finding, a letter) or a request for one.
            _title = "Text oder Anweisung sprechen" + _profileSuffix;
            _context = "Nichts markiert: schreibt neuen Text an die Cursorstelle";
            _contextWarn = true;
            _quickLabel = "Oder auf das Gesprochene anwenden:";
        }
        else
        {
            _context = $"Bearbeitet die Markierung ({selection.Length:N0} Zeichen)";
            _contextWarn = false;
            _quickLabel = "Oder direkt auf die Markierung anwenden:";
        }
        Present();
    }

    public void ShowProcessing(string title = "Wird verarbeitet …")
    {
        _phase = Phase.Processing;
        _title = title;
        _hint = "Kürzel drücken bricht ab";
        _context = null;
        Present();
    }

    /// <summary>
    /// Short confirmation, hides itself after a moment. <paramref name="neutral"/> shows an info mark
    /// instead of the green check: nothing went wrong, but nothing changed either.
    /// </summary>
    public void ShowDone(string title, bool neutral = false)
    {
        _phase = Phase.Done;
        _doneNeutral = neutral;
        _title = title;
        _hint = "";
        _context = null;
        _doneUntil = DateTime.UtcNow.AddMilliseconds(1100);
        Present();
    }

    /// <summary>
    /// Words of a language-model reply that were not dictated (<see cref="Core.DictationFidelityCheck"/>).
    /// Stays up for a while and closes on a click: the text is already in, this is a reminder to check it.
    /// </summary>
    public void ShowWarning(IReadOnlyList<string> words, bool pasted)
    {
        _phase = Phase.Warning;
        _title = "Nicht in deinem Diktat";
        _warningWords = string.Join(", ", words.Take(WarningMaxWords))
            + (words.Count > WarningMaxWords ? $" und {words.Count - WarningMaxWords} weitere" : "");
        _hint = (pasted ? "Eingefügt." : "In der Zwischenablage, mit Strg+V einfügen.") + " Vor dem Speichern prüfen.";
        _context = null;
        _warningUntil = DateTime.UtcNow + WarningShown;
        Present();
    }

    public void HideOverlay()
    {
        _phase = Phase.Hidden;
        _timer.Stop();
        Hide();
    }

    // ---------------- layout and painting ----------------

    private float S => DeviceDpi / 96f;

    // A one-word confirmation in a 400 px pill looks lost; fit the pill to its text.
    private int DoneWidth()
    {
        using var g = CreateGraphics();
        int text = TextRenderer.MeasureText(g, _title, _titleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        return Math.Clamp(text + (int)((38 + 20) * S), (int)(150 * S), (int)(400 * S));
    }

    private bool ShowsCard => _phase == Phase.Recording && _quick.Count > 0;

    // Card: header (34), context (20), divider (10), label (20), rows (24 each), gap (6), hint (20), bottom (8).
    private const float RowHeight = 24, CardTop = 84;

    private Size Measure()
    {
        float s = S;
        if (_phase == Phase.Done) return new Size(DoneWidth(), (int)(44 * s));
        if (_phase == Phase.Warning) return new Size((int)(460 * s), (int)((34 + WarningWordLines() * 19 + 4 + 20 + 8) * s));
        if (ShowsCard) return new Size((int)(440 * s), (int)((CardTop + _quick.Count * RowHeight + 6 + 20 + 8) * s));
        int lines = _context != null ? 2 : 1;
        return new Size((int)(400 * s), (int)((34 + 20 * lines + 8) * s));
    }

    private int WarningWordLines()
    {
        using var g = CreateGraphics();
        int width = (int)(460 * S - (18 + 20 + 18) * S);
        var size = TextRenderer.MeasureText(g, _warningWords, _textFont, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return size.Height > 22 * S ? 2 : 1;
    }

    private void Present()
    {
        var size = Measure();
        // On the screen the user is working on, centered above the taskbar.
        var screen = Screen.FromHandle(NativeMethods.GetForegroundWindow());
        var area = screen.WorkingArea;
        var location = new Point(area.Left + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(28 * S));
        if (Size != size || Location != location) SetBounds(location.X, location.Y, size.Width, size.Height);
        if (!Visible) Show();
        SetClickThrough(!(ShowsCard || _phase == Phase.Warning));
        _timer.Start();
        Invalidate();
        Core.Log.Info($"Overlay: {_phase} at {Bounds}, visible {Visible}");
    }

    private void OnTick()
    {
        _tick++;
        if ((_phase == Phase.Done && DateTime.UtcNow >= _doneUntil) || (_phase == Phase.Warning && DateTime.UtcNow >= _warningUntil))
        {
            HideOverlay();
            return;
        }
        if (_phase == Phase.Recording && _level != null)
        {
            // Fast attack, slow release, and a square root so normal speech fills the bars.
            float raw = MathF.Sqrt(Math.Clamp(_level(), 0f, 1f));
            _smoothed = raw > _smoothed ? raw : _smoothed * 0.8f + raw * 0.2f;
            _levels[_levelHead] = _smoothed;
            _levelHead = (_levelHead + 1) % Bars;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = S;
        float pad = 18 * s;
        float row1 = (_phase == Phase.Done ? 22 : 21) * s; // vertical centre of the first row
        float textX = pad + 20 * s;
        float right = Width - pad;

        DrawIndicator(g, new PointF(pad + 6 * s, row1), s);

        // Right side of row 1: elapsed time and level bars while recording.
        if (_phase == Phase.Recording)
        {
            var elapsed = DateTime.UtcNow - _started;
            var left = _limit - elapsed;
            bool nearLimit = left <= LimitWarning;
            string time = Format(elapsed);
            var timeSize = TextRenderer.MeasureText(g, time, _textFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, time, _textFont, new Point((int)(right - timeSize.Width), (int)(row1 - timeSize.Height / 2f)),
                nearLimit ? Amber : Secondary, TextFormatFlags.NoPadding);
            if (_level != null) DrawBars(g, right - timeSize.Width - 12 * s, row1, s);

            if (nearLimit && left > TimeSpan.Zero) _hint = $"Endet in {Format(left)} automatisch · Esc bricht ab";
        }

        var titleSize = TextRenderer.MeasureText(g, _title, _titleFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _title, _titleFont, new Point((int)textX, (int)(row1 - titleSize.Height / 2f)), Primary, TextFormatFlags.NoPadding);

        float y = row1 + 13 * s;
        int width = (int)(right - textX);
        if (_phase == Phase.Warning)
        {
            int lines = WarningWordLines();
            TextRenderer.DrawText(g, _warningWords, _textFont, new Rectangle((int)textX, (int)y, width, (int)(lines * 19 * s)), Primary,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            y += (lines * 19 + 4) * s;
        }
        if (_context != null)
        {
            DrawLine(g, _context, _contextWarn ? Amber : Secondary, textX, y, width);
            y += 20 * s;
        }
        if (ShowsCard)
        {
            y = DrawQuickActions(g, y, s);
        }

        string hint = _hint;
        Color hintColor = Muted;
        if (_phase == Phase.Recording && _peakSoFar != null && DateTime.UtcNow - _started > SilentAfter && _peakSoFar() < SilentPeak)
        {
            hint = "Kein Ton vom Mikrofon. Stumm geschaltet oder gesperrt?";
            hintColor = Amber;
        }
        if (hint.Length > 0) DrawLine(g, hint, hintColor, textX, y, width);
    }

    /// <summary>Divider, label and one row per quick instruction; returns the y below the last row.</summary>
    private float DrawQuickActions(Graphics g, float y, float s)
    {
        float pad = 18 * s;
        using (var line = new Pen(Border, 1f))
            g.DrawLine(line, pad, y + 3 * s, Width - pad, y + 3 * s);
        y += 10 * s;
        // Same column as title and context; the indicator gutter stays empty.
        float x = pad + 20 * s, right = Width - pad;
        DrawLine(g, _quickLabel, Muted, x, y, (int)(right - x));
        y += 20 * s;

        _rowRects.Clear();
        float keyWidth = TextRenderer.MeasureText(g, "Strg+5", _textFont, Size.Empty, TextFormatFlags.NoPadding).Width + 12 * s;
        for (int i = 0; i < _quick.Count; i++)
        {
            var row = new RectangleF(x - 8 * s, y, right - x + 16 * s, RowHeight * s - 2 * s);
            _rowRects.Add(row);
            if (i == _hover)
            {
                using var fill = new SolidBrush(Color.FromArgb(52, 52, 52));
                using var path = Rounded(row, 6 * s);
                g.FillPath(fill, path);
            }
            float mid = row.Y + row.Height / 2;
            string key = $"Strg+{i + 1}";
            var keySize = TextRenderer.MeasureText(g, key, _textFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, key, _textFont, new Point((int)x, (int)(mid - keySize.Height / 2f)), Muted, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _quick[i], _textFont,
                new Rectangle((int)(x + keyWidth), (int)(mid - keySize.Height / 2f), (int)(right - x - keyWidth), keySize.Height + 2),
                i == _hover ? Primary : Secondary, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += RowHeight * s;
        }
        return y + 6 * s;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = ShowsCard ? _rowRects.FindIndex(r => r.Contains(e.Location)) : -1;
        Cursor = hover >= 0 || _phase == Phase.Warning ? Cursors.Hand : Cursors.Default;
        if (hover == _hover) return;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_phase == Phase.Warning)
        {
            HideOverlay();
            return;
        }
        if (!ShowsCard) return;
        int row = _rowRects.FindIndex(r => r.Contains(e.Location));
        if (row >= 0) QuickActionClicked?.Invoke(row);
    }

    private void SetClickThrough(bool through)
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        int want = through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) SetWindowLong(Handle, GWL_EXSTYLE, want);
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

    private void DrawIndicator(Graphics g, PointF c, float s)
    {
        switch (_phase)
        {
            case Phase.Warning:
            {
                // Amber triangle with an exclamation mark: look again, nothing failed.
                float r = 8.5f * s;
                using var tri = new GraphicsPath();
                tri.AddPolygon(new[] { new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y + r * 0.8f), new PointF(c.X - r, c.Y + r * 0.8f) });
                using var fill = new SolidBrush(Amber);
                g.FillPath(fill, tri);
                using var mark = new Pen(Back, 1.9f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(mark, c.X, c.Y - 3.2f * s, c.X, c.Y + 1.6f * s);
                using var dot = new SolidBrush(Back);
                float d = 1.15f * s;
                g.FillEllipse(dot, c.X - d, c.Y + 4.4f * s - d, d * 2, d * 2);
                break;
            }
            case Phase.Recording:
            {
                // Slow pulse: alive, but not nervous.
                float pulse = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(_tick * 0.16f));
                float r = 5.5f * s;
                using var halo = new SolidBrush(Color.FromArgb((int)(70 * pulse), Red));
                g.FillEllipse(halo, c.X - r * 1.8f, c.Y - r * 1.8f, r * 3.6f, r * 3.6f);
                using var dot = new SolidBrush(Red);
                g.FillEllipse(dot, c.X - r, c.Y - r, r * 2, r * 2);
                break;
            }
            case Phase.Processing:
            {
                float r = 6.5f * s;
                using var track = new Pen(Color.FromArgb(70, Amber), 2.2f * s);
                using var arc = new Pen(Amber, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var box = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
                g.DrawEllipse(track, box);
                g.DrawArc(arc, box, _tick * 18 % 360, 100);
                break;
            }
            case Phase.Done when _doneNeutral:
            {
                float r = 8f * s;
                using var circle = new SolidBrush(Muted);
                g.FillEllipse(circle, c.X - r, c.Y - r, r * 2, r * 2);
                using var mark = new Pen(Back, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(mark, c.X, c.Y - 0.5f * s, c.X, c.Y + 3.8f * s);
                using var dot = new SolidBrush(Back);
                float d = 1.3f * s;
                g.FillEllipse(dot, c.X - d, c.Y - 4.6f * s - d, d * 2, d * 2);
                break;
            }
            case Phase.Done:
            {
                float r = 8f * s;
                using var circle = new SolidBrush(Green);
                g.FillEllipse(circle, c.X - r, c.Y - r, r * 2, r * 2);
                using var tick = new Pen(Back, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(c.X - 3.6f * s, c.Y + 0.2f * s),
                    new PointF(c.X - 1f * s, c.Y + 2.8f * s),
                    new PointF(c.X + 3.8f * s, c.Y - 2.6f * s),
                });
                break;
            }
        }
    }

    /// <summary>Scrolling level history, newest on the right: shows at a glance that the mic hears you.</summary>
    private void DrawBars(Graphics g, float rightEdge, float centerY, float s)
    {
        float barW = 2f * s, gap = 2f * s, maxH = 18f * s, minH = 2f * s;
        float x = rightEdge - Bars * (barW + gap);
        using var brush = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
        for (int i = 0; i < Bars; i++)
        {
            float v = _levels[(_levelHead + i) % Bars];
            float h = Math.Max(minH, v * maxH);
            using var path = RoundedBar(x + i * (barW + gap), centerY - h / 2, barW, h);
            g.FillPath(brush, path);
        }
    }

    private static GraphicsPath RoundedBar(float x, float y, float w, float h)
    {
        var path = new GraphicsPath();
        float d = Math.Min(w, h);
        path.AddArc(x, y, d, d, 180, 180);
        path.AddArc(x, y + h - d, d, d, 0, 180);
        path.CloseFigure();
        return path;
    }

    private void DrawLine(Graphics g, string text, Color color, float x, float y, int width) =>
        TextRenderer.DrawText(g, text, _textFont, new Rectangle((int)x, (int)y, width, (int)(18 * S)), color,
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
