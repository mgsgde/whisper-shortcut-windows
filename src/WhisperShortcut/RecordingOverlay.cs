using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
/// The window is a per-pixel-alpha layered window: the pill is drawn into a bitmap (true round ends,
/// a soft shadow, fade and slide in and out) and handed to UpdateLayeredWindow.
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private enum Phase { Hidden, Recording, Processing, Done, Warning }

    private static readonly Color Primary = Color.FromArgb(247, 247, 250);
    private static readonly Color Secondary = Color.FromArgb(205, 205, 212);
    private static readonly Color Muted = Color.FromArgb(142, 142, 152);
    private static readonly Color Red = Color.FromArgb(255, 82, 72);
    private static readonly Color Amber = Color.FromArgb(255, 189, 70);
    private static readonly Color Green = Color.FromArgb(52, 211, 105);

    private const int Bars = 24;
    // Under 1 % peak after 3 s: not a quiet speaker, a muted or blocked microphone (same bar as MicAudioSource).
    private const float SilentPeak = 0.01f;
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LimitWarning = TimeSpan.FromSeconds(60);
    // Long enough to read eight words and compare them with the note; a click closes it earlier.
    private static readonly TimeSpan WarningShown = TimeSpan.FromSeconds(20);
    private const int WarningMaxWords = 8;

    // Layout in 96-dpi pixels; everything is multiplied by S.
    private const float ShadowMargin = 24;  // room for the shadow around the card
    private const float HeaderH = 58;       // title + hint row
    private const float DoneH = 46;
    private const float ContextH = 26;
    private const float RowH = 28;
    private const float TextX = 52;         // text column, right of the indicator
    private const float FadeIn = 0.16f, FadeOut = 0.2f;

    private static readonly string BodyFace =
        FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly float[] _levels = new float[Bars];
    private readonly StringFormat _line = new(StringFormat.GenericTypographic)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
    };
    private readonly StringFormat _wrap = new(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };

    private Font? _titleFont, _bodyFont, _smallFont;
    private int _fontDpi;

    private Phase _phase = Phase.Hidden;
    private string _title = "";
    private string _hint = "";
    private string _stopKey = "";
    private string? _context;
    // The recording applies a dictation template (title and selection line differ from Dictate Prompt).
    private bool _template;
    private bool _contextWarn;
    // A held screenshot goes along with this run: one extra line says so (patient data leaves the PC with it).
    private bool _screenshot;
    private const string ScreenshotLine = "Bildschirmfoto angehängt, geht mit an das Sprachmodell";
    private Func<float>? _level;
    private Func<float>? _peakSoFar;
    private TimeSpan _limit;
    private DateTime _started;
    private DateTime _phaseStart;
    private DateTime _appearStart;
    private DateTime? _fadeStart;
    private DateTime _doneUntil;
    private bool _doneNeutral;
    private int _levelHead;
    private float _smoothed;
    private IReadOnlyList<string> _quick = Array.Empty<string>();
    private string _quickLabel = "";
    private readonly List<RectangleF> _rowRects = new();
    private int _hover = -1;
    private string _warningWords = "";
    private DateTime _warningUntil;
    private Point _basePos;

    /// <summary>A quick instruction row was clicked (index into the list given to ShowRecording).</summary>
    public event Action<int>? QuickActionClicked;

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AccessibleName = Core.UserCopy.OverlayAccessibleName;
        AccessibleRole = AccessibleRole.StatusBar;
        _timer.Tick += (_, _) => OnTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
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

    // The bitmap goes out through UpdateLayeredWindow; there is nothing for WM_PAINT to do.
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // ---------------- API for TrayApp ----------------

    /// <param name="level">Live level 0..1 (null for a test WAV: no bars, no silence warning).</param>
    /// <param name="quickActions">Dictate Prompt: the instructions offered as Ctrl+1–5 rows (empty: plain pill).</param>
    /// <param name="templateName">A template recording: its name is the title, and no quick instructions are offered.</param>
    /// <param name="screenshot">A held screenshot is sent with this run: shown as its own line.</param>
    public void ShowRecording(Mode mode, string stopKey, TimeSpan limit, Func<float>? level, Func<float>? peakSoFar,
        IReadOnlyList<string>? quickActions = null, string? templateName = null, bool screenshot = false)
    {
        _phase = Phase.Recording;
        _screenshot = screenshot && mode != Mode.Dictate;
        _template = templateName != null;
        _title = _template ? Core.UserCopy.OverlayTemplateTitle(templateName!)
            : mode == Mode.Dictate ? Core.UserCopy.OverlayDictateTitle : Core.UserCopy.OverlayPromptTitle;
        _stopKey = stopKey;
        _hint = Core.UserCopy.OverlayStopHint(stopKey);
        // Dictate Prompt and templates: the selection is known only after Ctrl+C (up to 0.5 s), see SetSelection.
        _context = mode != Mode.Dictate ? "Markierung wird gelesen …" : null;
        if (_template) quickActions = null;
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

    /// <summary>Dictate Prompt or a template: the selection is known. null = nothing selected.</summary>
    public void SetSelection(string? selection)
    {
        if (_phase != Phase.Recording || _context == null) return;
        if (_template)
        {
            // A template formats what it gets; with nothing marked that is the dictation, which is the normal case.
            _context = selection == null
                ? "Nichts markiert: das Diktat kommt in dieses Format"
                : $"Bringt die Markierung in dieses Format ({selection.Length.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} Zeichen)";
            _contextWarn = false;
        }
        else if (selection == null)
        {
            // Nothing to edit: what the user says is the text (a finding, a letter) or a request for one.
            _title = "Text oder Anweisung sprechen";
            _context = "Nichts markiert: schreibt neuen Text an die Cursorstelle";
            _contextWarn = true;
            _quickLabel = "Oder auf das Gesprochene anwenden:";
        }
        else
        {
            _context = $"Bearbeitet die Markierung ({selection.Length.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} Zeichen)";
            _contextWarn = false;
            _quickLabel = "Oder direkt auf die Markierung anwenden:";
        }
        Present();
    }

    public void ShowProcessing(string title = Core.UserCopy.Processing)
    {
        _phase = Phase.Processing;
        _title = title;
        _hint = Core.UserCopy.OverlayCancelHint;
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
        _fadeStart = null;
        _timer.Stop();
        Hide();
    }

    // ---------------- layout ----------------

    private float S => DeviceDpi / 96f;

    private static bool Hc => SystemInformation.HighContrast;
    private static Color CPrimary => Hc ? SystemColors.WindowText : Primary;
    private static Color CSecondary => Hc ? SystemColors.WindowText : Secondary;
    private static Color CMuted => Hc ? SystemColors.GrayText : Muted;
    private static Color CRed => Hc ? SystemColors.HotTrack : Red;
    private static Color CAmber => Hc ? SystemColors.WindowText : Amber;
    private static Color CGreen => Hc ? SystemColors.WindowText : Green;

    private bool ShowsCard => _phase == Phase.Recording && _quick.Count > 0;

    private bool ShowsScreenshot => _phase == Phase.Recording && _screenshot;

    // Plain pill: nothing below the header. Everything else is a card with softer corners.
    private bool IsPill => !ShowsCard && _phase != Phase.Warning && _context == null && !ShowsScreenshot;

    private void EnsureFonts()
    {
        if (_titleFont != null && _fontDpi == DeviceDpi) return;
        _titleFont?.Dispose();
        _bodyFont?.Dispose();
        _smallFont?.Dispose();
        float k = S;
        _fontDpi = DeviceDpi;
        _titleFont = new Font("Segoe UI Semibold", 15f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font(BodyFace, 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font(BodyFace, 11f * k, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private float MeasureWidth(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, new SizeF(10000, 1000), _line).Width;

    private int WarningLines(Graphics g, float cardWidth)
    {
        float width = cardWidth - (TextX + 24) * S;
        float h = g.MeasureString(_warningWords, _bodyFont!, new SizeF(width, 1000), _wrap).Height;
        return Math.Clamp((int)Math.Ceiling(h / (17.5f * S)), 1, 3);
    }

    /// <summary>Size of the card itself, without the shadow margin.</summary>
    private SizeF CardSize(Graphics g)
    {
        float s = S;
        if (_phase == Phase.Done)
        {
            float text = MeasureWidth(g, _title, _titleFont!);
            return new SizeF(Math.Clamp(text + (TextX - 6 + 26) * s, 150 * s, 420 * s), DoneH * s);
        }
        if (_phase == Phase.Warning)
        {
            float w = 480 * s;
            return new SizeF(w, (HeaderH + WarningLines(g, w) * 18 + 14) * s);
        }
        float shot = ShowsScreenshot ? ContextH : 0;
        if (ShowsCard) return new SizeF(500 * s, (HeaderH + ContextH + shot + 4 + 28 + _quick.Count * RowH + 12) * s);
        return new SizeF(500 * s, (HeaderH + (_context != null ? ContextH : 0) + shot + (_context != null || shot > 0 ? 6 : 0)) * s);
    }

    private void Present()
    {
        EnsureFonts();
        using var probe = LayeredWindow.NewBitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        ConfigureText(pg);
        var card = CardSize(pg);
        int m = (int)Math.Ceiling(ShadowMargin * S);
        var size = new Size((int)Math.Ceiling(card.Width) + 2 * m, (int)Math.Ceiling(card.Height) + 2 * m);
        // On the screen the user is working on, centered above the taskbar.
        var screen = Screen.FromHandle(NativeMethods.GetForegroundWindow());
        var area = screen.WorkingArea;
        _basePos = new Point(area.Left + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(14 * S) + m / 2);
        if (Size != size || Location != _basePos) SetBounds(_basePos.X, _basePos.Y, size.Width, size.Height);

        _phaseStart = DateTime.UtcNow;
        _fadeStart = null;
        AccessibleDescription = Core.UserCopy.OverlayAnnouncement(_title, _hint, _context);
        if (!Visible)
        {
            _appearStart = DateTime.UtcNow;
            Show();
        }
        SetClickThrough(!(ShowsCard || _phase == Phase.Warning));
        _timer.Start();
        Redraw();
        Core.Log.Info($"Overlay: {_phase} at {Bounds}, visible {Visible}");
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        if (_fadeStart == null && ((_phase == Phase.Done && now >= _doneUntil) || (_phase == Phase.Warning && now >= _warningUntil)))
            _fadeStart = now;
        if (_fadeStart != null && (now - _fadeStart.Value).TotalSeconds >= FadeOut)
        {
            HideOverlay();
            return;
        }
        if (_phase == Phase.Recording && _level != null)
        {
            // Fast attack, slow release, and a square root so normal speech fills the bars.
            float raw = MathF.Sqrt(Math.Clamp(_level(), 0f, 1f));
            _smoothed = raw > _smoothed ? raw : _smoothed * 0.82f + raw * 0.18f;
            _levels[_levelHead] = _smoothed;
            _levelHead = (_levelHead + 1) % Bars;
        }
        Redraw();
    }

    // ---------------- painting ----------------

    private static void ConfigureText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0f, 1f), 3);

    private void Redraw()
    {
        if (!IsHandleCreated || !Visible || _phase == Phase.Hidden || Width <= 0 || Height <= 0) return;
        EnsureFonts();
        float s = S;
        var now = DateTime.UtcNow;
        float appear = Ease((float)(now - _appearStart).TotalSeconds / FadeIn);
        float fade = _fadeStart == null ? 1f : 1f - Ease((float)(now - _fadeStart.Value).TotalSeconds / FadeOut);

        using var bmp = LayeredWindow.NewBitmap(Width, Height);
        using (var g = Graphics.FromImage(bmp))
        {
            ConfigureText(g);
            float m = ShadowMargin * s;
            var card = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            PaintCard(g, card, s);
        }
        // Slide up by a few pixels while fading in, and sink slightly while fading out.
        int slide = (int)((1 - appear) * 10 * s + (1 - fade) * 6 * s);
        LayeredWindow.Update(Handle, bmp, new Point(_basePos.X, _basePos.Y + slide), (byte)Math.Clamp(255 * appear * fade, 0, 255));
    }

    private void PaintCard(Graphics g, RectangleF card, float s)
    {
        float radius = IsPill ? card.Height / 2 : 24 * s;
        var now = DateTime.UtcNow;

        if (!Hc) LayeredWindow.Shadow(g, card, radius, s);

        using (var path = Rounded(card, radius))
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
                using var fill = new LinearGradientBrush(card, Color.FromArgb(255, 42, 42, 49), Color.FromArgb(255, 24, 24, 29), 90f);
                g.FillPath(fill, path);
            }
        }
        if (!Hc)
        {
            // Hairline border, a touch brighter along the top edge like light on glass.
            var inner = RectangleF.Inflate(card, -0.5f, -0.5f);
            using var path = Rounded(inner, radius - 0.5f);
            using var edge = new LinearGradientBrush(card, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(22, 255, 255, 255), 90f);
            using var pen = new Pen(edge, 1f);
            g.DrawPath(pen, path);
        }

        float x = card.X, y = card.Y, right = card.Right;
        float headerCy = y + (_phase == Phase.Done ? DoneH / 2 : HeaderH / 2) * s;
        float textX = x + TextX * s;
        DrawIndicator(g, new PointF(x + 29 * s, headerCy), s, now);

        float titleMax = right - textX - 20 * s;
        if (_phase == Phase.Recording)
        {
            var elapsed = now - _started;
            var left = _limit - elapsed;
            bool nearLimit = left <= LimitWarning;
            string time = Format(elapsed);
            float timeW = MeasureWidth(g, time, _bodyFont!);
            DrawText(g, time, _bodyFont!, nearLimit ? CAmber : CSecondary, right - 24 * s - timeW, headerCy, timeW + 4 * s);
            if (_level != null)
            {
                float barsRight = right - 24 * s - timeW - 16 * s;
                DrawBars(g, barsRight, headerCy, s);
                titleMax = barsRight - Bars * 5.5f * s - textX - 8 * s;
            }
            else titleMax = right - 24 * s - timeW - textX - 8 * s;
        }

        bool hasHint = _phase != Phase.Done;
        float titleCy = hasHint ? y + 21 * s : headerCy;
        DrawText(g, _title, _titleFont!, CPrimary, textX, titleCy, titleMax);

        float yCursor = y + HeaderH * s;
        if (_phase == Phase.Warning)
        {
            int lines = WarningLines(g, card.Width);
            using var br = new SolidBrush(CPrimary);
            g.DrawString(_warningWords, _bodyFont!, br, new RectangleF(textX, y + (HeaderH - 4) * s, right - textX - 24 * s, lines * 18 * s), _wrap);
        }
        if (hasHint) DrawHint(g, textX, y + 39 * s, right - textX - 20 * s, s);

        if (_context != null)
        {
            float cy = yCursor + ContextH / 2 * s - 2 * s;
            DrawText(g, _context, _bodyFont!, _contextWarn ? CAmber : CSecondary, textX, cy, right - textX - 24 * s);
            yCursor += ContextH * s;
        }
        if (ShowsScreenshot)
        {
            DrawText(g, ScreenshotLine, _bodyFont!, CSecondary, textX, yCursor + ContextH / 2 * s - 2 * s, right - textX - 24 * s);
            yCursor += ContextH * s;
        }
        if (ShowsCard) DrawQuickActions(g, card, yCursor + 4 * s, s);
    }

    /// <summary>The line under the title: stop key and Esc as key caps while recording, plain text otherwise.</summary>
    private void DrawHint(Graphics g, float x, float cy, float maxW, float s)
    {
        if (_phase == Phase.Recording)
        {
            if (_peakSoFar != null && DateTime.UtcNow - _started > SilentAfter && _peakSoFar() < SilentPeak)
            {
                DrawText(g, "Kein Ton vom Mikrofon. Stumm geschaltet oder gesperrt?", _smallFont!, CAmber, x, cy, maxW);
                return;
            }
            var left = _limit - (DateTime.UtcNow - _started);
            if (left <= LimitWarning && left > TimeSpan.Zero)
            {
                DrawText(g, $"Endet in {Format(left)} automatisch · Esc bricht ab", _smallFont!, CAmber, x, cy, maxW);
                return;
            }
            x = KeyCap(g, _stopKey, x, cy, s) + 6 * s;
            DrawText(g, "beendet", _smallFont!, CMuted, x, cy, maxW);
            x += MeasureWidth(g, "beendet", _smallFont!) + 14 * s;
            x = KeyCap(g, "Esc", x, cy, s) + 6 * s;
            DrawText(g, "bricht ab", _smallFont!, CMuted, x, cy, maxW);
            return;
        }
        DrawText(g, _hint, _smallFont!, CMuted, x, cy, maxW);
    }

    /// <summary>A small rounded key cap; returns the x of its right edge.</summary>
    private float KeyCap(Graphics g, string label, float x, float cy, float s)
    {
        float tw = MeasureWidth(g, label, _smallFont!);
        var r = new RectangleF(x, cy - 9 * s, tw + 12 * s, 18 * s);
        using (var path = Rounded(r, 5 * s))
        {
            using var fill = new SolidBrush(Hc ? SystemColors.Window : Color.FromArgb(34, 255, 255, 255));
            g.FillPath(fill, path);
            using var pen = new Pen(Hc ? SystemColors.WindowFrame : Color.FromArgb(46, 255, 255, 255), 1f);
            g.DrawPath(pen, path);
        }
        DrawText(g, label, _smallFont!, CSecondary, x + 6 * s, cy, tw + 4 * s);
        return r.Right;
    }

    /// <summary>Divider, label and one row per quick instruction.</summary>
    private void DrawQuickActions(Graphics g, RectangleF card, float y, float s)
    {
        using (var line = new Pen(Hc ? SystemColors.WindowFrame : Color.FromArgb(30, 255, 255, 255), 1f))
            g.DrawLine(line, card.X + 20 * s, y, card.Right - 20 * s, y);
        float textX = card.X + TextX * s, right = card.Right - 24 * s;
        DrawText(g, _quickLabel, _smallFont!, CMuted, textX, y + 16 * s, right - textX);
        y += 30 * s;

        _rowRects.Clear();
        float chipW = MeasureWidth(g, "Strg+5", _smallFont!) + 14 * s;
        for (int i = 0; i < _quick.Count; i++)
        {
            var row = new RectangleF(card.X + 14 * s, y, card.Width - 28 * s, RowH * s - 2 * s);
            _rowRects.Add(row);
            if (i == _hover)
            {
                using var fill = new SolidBrush(Hc ? SystemColors.Highlight : Color.FromArgb(30, 255, 255, 255));
                using var path = Rounded(row, 9 * s);
                g.FillPath(fill, path);
            }
            float mid = row.Y + row.Height / 2;
            var cap = new RectangleF(textX, mid - 9 * s, chipW, 18 * s);
            using (var path = Rounded(cap, 5 * s))
            {
                using var fill = new SolidBrush(Color.FromArgb(i == _hover ? 52 : 30, 255, 255, 255));
                g.FillPath(fill, path);
            }
            DrawText(g, $"Strg+{i + 1}", _smallFont!, i == _hover ? CPrimary : CMuted, cap.X + 7 * s, mid, chipW);
            Color text = i == _hover ? (Hc ? SystemColors.HighlightText : CPrimary) : CSecondary;
            DrawText(g, _quick[i], _bodyFont!, text, cap.Right + 12 * s, mid, right - cap.Right - 12 * s);
            y += RowH * s;
        }
    }

    /// <summary>One line of text, vertically centred on <paramref name="cy"/>, cut off with an ellipsis at <paramref name="maxW"/>.</summary>
    private void DrawText(Graphics g, string text, Font font, Color color, float x, float cy, float maxW)
    {
        if (maxW <= 0) return;
        float h = font.GetHeight(g);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(x, cy - h / 2, maxW, h + 2), _line);
    }

    private void DrawIndicator(Graphics g, PointF c, float s, DateTime now)
    {
        float since = (float)(now - _phaseStart).TotalSeconds;
        switch (_phase)
        {
            case Phase.Warning:
            {
                // Amber disc with an exclamation mark: look again, nothing failed.
                float r = 10f * s;
                using var fill = new SolidBrush(CAmber);
                g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
                using var mark = new Pen(Hc ? SystemColors.Window : Color.FromArgb(30, 30, 36), 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(mark, c.X, c.Y - 4.6f * s, c.X, c.Y + 1.2f * s);
                using var dot = new SolidBrush(mark.Color);
                float d = 1.2f * s;
                g.FillEllipse(dot, c.X - d, c.Y + 4.4f * s - d, d * 2, d * 2);
                break;
            }
            case Phase.Recording:
            {
                // Two soft rings drift outwards from the dot: alive, but not nervous.
                float t = (float)(now - _started).TotalSeconds;
                float r = 5.5f * s;
                for (int ring = 0; ring < 2; ring++)
                {
                    float p = (t * 0.75f + ring * 0.5f) % 1f;
                    float rr = r + p * 10 * s;
                    using var pen = new Pen(Color.FromArgb((int)(120 * (1 - p) * (1 - p)), CRed), 1.6f * s);
                    g.DrawEllipse(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2);
                }
                using var dot = new SolidBrush(CRed);
                g.FillEllipse(dot, c.X - r, c.Y - r, r * 2, r * 2);
                break;
            }
            case Phase.Processing:
            {
                float r = 8f * s;
                var box = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
                using var track = new Pen(Color.FromArgb(60, CAmber), 2.4f * s);
                using var arc = new Pen(CAmber, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawEllipse(track, box);
                g.DrawArc(arc, box, since * 380f % 360f, 96);
                break;
            }
            case Phase.Done:
            {
                // The disc pops in with a little overshoot, the mark follows.
                float k = Math.Clamp(since / 0.22f, 0f, 1f);
                float pop = 1 + 0.18f * MathF.Sin(k * MathF.PI) * (1 - k);
                float r = 10f * s * Ease(k * 1.4f) * pop;
                Color tone = _doneNeutral ? CMuted : CGreen;
                using var circle = new SolidBrush(tone);
                g.FillEllipse(circle, c.X - r, c.Y - r, r * 2, r * 2);
                var ink = Hc ? SystemColors.Window : Color.FromArgb(24, 24, 29);
                using var mark = new Pen(ink, 2.1f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float q = Ease(k * 1.4f);
                if (_doneNeutral)
                {
                    g.DrawLine(mark, c.X, c.Y - 0.5f * s * q, c.X, c.Y + 4f * s * q);
                    using var dot = new SolidBrush(ink);
                    float d = 1.3f * s * q;
                    g.FillEllipse(dot, c.X - d, c.Y - 4.8f * s - d, d * 2, d * 2);
                }
                else
                {
                    g.DrawLines(mark, new[]
                    {
                        new PointF(c.X - 4f * s * q, c.Y + 0.3f * s * q),
                        new PointF(c.X - 1.2f * s * q, c.Y + 3.1f * s * q),
                        new PointF(c.X + 4.2f * s * q, c.Y - 2.8f * s * q),
                    });
                }
                break;
            }
        }
    }

    /// <summary>Scrolling level history, newest on the right: shows at a glance that the mic hears you.</summary>
    private void DrawBars(Graphics g, float rightEdge, float centerY, float s)
    {
        float barW = 3f * s, gap = 2.5f * s, maxH = 28f * s, minH = 3f * s;
        float x = rightEdge - Bars * (barW + gap) + gap;
        for (int i = 0; i < Bars; i++)
        {
            float v = _levels[(_levelHead + i) % Bars];
            float h = Math.Max(minH, v * maxH);
            // Older bars fade out to the left.
            float age = (float)(i + 1) / Bars;
            int alpha = Hc ? 255 : (int)(60 + 195 * MathF.Pow(age, 1.3f));
            var tone = Hc ? CPrimary : Color.FromArgb(alpha, 255, 255, 255);
            using var brush = new SolidBrush(tone);
            using var path = RoundedBar(x + i * (barW + gap), centerY - h / 2, barW, h);
            g.FillPath(brush, path);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------------- mouse ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = ShowsCard ? _rowRects.FindIndex(r => r.Contains(e.Location)) : -1;
        Cursor = hover >= 0 || _phase == Phase.Warning ? Cursors.Hand : Cursors.Default;
        if (hover == _hover) return;
        _hover = hover;
        Redraw();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Redraw();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
            _smallFont?.Dispose();
            _line.Dispose();
            _wrap.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
