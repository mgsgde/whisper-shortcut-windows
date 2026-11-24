using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// The pill at the bottom of the screen, drawn like the Mac's recording pill (RecordingIndicatorWindow.swift):
/// a black capsule (92 % opaque) with ✕, ten white level bars, the status word and a white ✓ while recording,
/// ✕, spinner and status word while processing. Dictate Prompt and templates grow it into the Mac's card
/// (QuickActionCardView): what the run acts on, the three most used quick instructions (Strg+1–3 or a click), and the recording
/// controls as its footer.
/// Like the Mac panel (borderless, non-activating, hasShadow) it never takes focus (WS_EX_NOACTIVATE,
/// MA_NOACTIVATE), otherwise Ctrl+V would land in the pill instead of the user's window.
/// The window is a per-pixel-alpha layered window: the Mac draws a solid black fill, not a blur material, so no
/// Acrylic/Mica is needed; a layered bitmap gives true capsule ends and a 16 px card radius (DWM rounding stops
/// at 8 px), plus a soft shadow and the Mac's 0.18 s fade.
/// Windows keeps (macos-paritaet D): the muted-microphone and time-limit warnings (amber, in place of the status
/// word), the short check mark when the text is in, and the "Nicht in deinem Diktat" warning card.
/// </summary>
internal sealed class RecordingOverlay : OverlayForm
{
    private enum Phase { Hidden, Recording, Processing, Done, Warning }
    private enum Context { None, Reading, Selection, Nothing }

    // Mac colours: Color.black.opacity(0.92), Color(white: 0.28), the warning amber of QuickActionCardView.
    private static readonly Color Fill = Color.FromArgb(235, 0, 0, 0);
    private static readonly Color Disc = Color.FromArgb(255, 71, 71, 71);
    private static readonly Color Amber = Color.FromArgb(255, 199, 89);
    private static readonly Color Green = Color.FromArgb(52, 211, 105);
    private static readonly Color MacAccent = Color.FromArgb(10, 132, 255);

    private const int Bars = 10;            // Mac RecordingIndicatorModel.barCount
    private const int TicksPerBar = 3;      // a new bar every 0.1 s at 30 fps
    // Under 1 % peak after 3 s: not a quiet speaker, a muted or blocked microphone (same bar as MicAudioSource).
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LimitWarning = TimeSpan.FromSeconds(60);
    // Long enough to read eight words and compare them with the note; a click closes it earlier.
    private static readonly TimeSpan WarningShown = TimeSpan.FromSeconds(20);
    private const int WarningMaxWords = 8;

    // Layout in 96-dpi pixels (Mac points); everything is multiplied by S.
    private const float ShadowMargin = 24;
    private const float PillH = 40;
    private const float PillPad = 8, Gap = 8;
    private const float MinRecordingPill = 218, MinProcessingPill = 168;
    private const float Button = 24;
    private const float CardW = 360, WarningW = 400;
    private const float CardPad = 4, CardRadius = 16, RowRadius = 7;
    private const float HeaderH = 28, RowH = 28, FooterH = 40;
    private const float DividerH = 1 + 4 * 2;
    private const float BottomMargin = 28;  // Mac Constants.bottomMargin
    private const float Fade = 0.18f;       // Mac Constants.fadeDuration
    private const float DonePop = 0.22f;

    private static readonly string TextFace = Pick("Segoe UI Variable Text", "Segoe UI");
    private static readonly string SemiboldFace = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    private static readonly string IconFace = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    private static string Pick(string preferred, string fallback) =>
        FontFamily.Families.Any(f => f.Name == preferred) ? preferred : fallback;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly float[] _levels = new float[Bars];
    private readonly float[] _shown = new float[Bars];
    private readonly StringFormat _line = new(StringFormat.GenericTypographic)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces,
    };
    private readonly StringFormat _wrap = new(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };

    // Mac sizes 10/11/12 pt; one pixel more on Windows, where Segoe at 11 px reads smaller than SF at 11 pt.
    private Font? _label, _labelRegular, _row, _small, _icon;
    private int _fontDpi;

    private Phase _phase = Phase.Hidden;
    private bool _card;
    private string _title = "";
    private string _hint = "";
    private string _stopKey = "";
    private string? _templateName;
    private Context _context;
    private string _selection = "";
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
    private int _tick;
    private readonly Core.LevelMeter _meter = new(0.82f);
    private IReadOnlyList<string> _quick = Array.Empty<string>();
    private readonly List<RectangleF> _rowRects = new();
    private int _hover = -1;
    private string _warningWords = "";
    private DateTime _warningUntil;
    private Point _basePos;
    private string _laidOutFor = "";
    // Shadow, fill and border change only with the size: drawn once, then copied each frame (fc69360).
    private Bitmap? _cardCache;
    private (Size Window, RectangleF Card, float Radius, bool Hc) _cardKey;
    private bool _settled;

    private RectangleF _cancelRect, _confirmRect;
    private int _hot; // 0 none, 1 ✕, 2 ✓

    /// <summary>A quick instruction row was clicked (index into the list given to ShowRecording).</summary>
    public event Action<int>? QuickActionClicked;

    /// <summary>The ✕ was clicked: discard the recording or cancel the processing (same as Esc).</summary>
    public event Action? CancelClicked;

    /// <summary>The ✓ was clicked: stop and process (same as the stop shortcut).</summary>
    public event Action? ConfirmClicked;

    public RecordingOverlay() : base(layered: true, clickThrough: true)
    {
        AccessibleName = Core.UserCopy.OverlayAccessibleName;
        AccessibleRole = AccessibleRole.StatusBar;
        _timer.Tick += (_, _) => OnTick();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // ---------------- API for TrayApp ----------------

    /// <param name="level">Live level 0..1 (null for a test WAV: no bars, no silence warning).</param>
    /// <param name="quickActions">Dictate Prompt: the card's rows (Take.Card, at most three).</param>
    /// <param name="templateName">A template recording: the card names it, and no quick instructions are offered.</param>
    /// <param name="screenshot">A held screenshot is sent with this run: shown as its own line.</param>
    public void ShowRecording(Core.TakeProfile profile, string stopKey, TimeSpan limit, Func<float>? level, Func<float>? peakSoFar,
        IReadOnlyList<string>? quickActions = null, string? templateName = null, bool screenshot = false, string? title = null)
    {
        _phase = Phase.Recording;
        _screenshot = screenshot && profile.TakesScreenshot;
        _templateName = templateName;
        _card = profile.CopiesSelection;
        _title = templateName != null ? Core.UserCopy.OverlayTemplateTitle(templateName)
            : profile.Family == Core.HotkeyFamily.Dictate ? Core.UserCopy.OverlayDictateTitle : Core.UserCopy.OverlayPromptTitle;
        if (title != null) _title = title;
        _stopKey = stopKey;
        _hint = Core.UserCopy.OverlayStopHint(stopKey);
        // Dictate Prompt and templates: the selection is known only after Ctrl+C (up to 0.5 s), see SetSelection.
        _context = _card ? Context.Reading : Context.None;
        _selection = "";
        _quick = profile.OffersQuickActions && templateName == null ? quickActions ?? Array.Empty<string>() : Array.Empty<string>();
        _hover = -1;
        _level = level;
        _peakSoFar = peakSoFar;
        _limit = limit;
        _started = DateTime.UtcNow;
        Array.Clear(_levels);
        Array.Clear(_shown);
        _meter.Reset();
        Present();
    }

    /// <summary>Dictate Prompt or a template: the selection is known. null = nothing selected.</summary>
    public void SetSelection(string? selection)
    {
        if (_phase != Phase.Recording || _context == Context.None) return;
        _context = selection == null ? Context.Nothing : Context.Selection;
        _selection = selection ?? "";
        Present();
    }

    public void ShowProcessing(string title = Core.UserCopy.Processing)
    {
        _phase = Phase.Processing;
        _card = false;
        _title = title;
        _hint = Core.UserCopy.OverlayCancelHint;
        _context = Context.None;
        Present();
    }

    /// <summary>
    /// Short confirmation, hides itself after a moment. <paramref name="neutral"/> shows an info mark
    /// instead of the green check: nothing went wrong, but nothing changed either.
    /// </summary>
    public void ShowDone(string title, bool neutral = false)
    {
        _phase = Phase.Done;
        _card = false;
        _doneNeutral = neutral;
        _title = title;
        _hint = "";
        _context = Context.None;
        _screenshot = false;
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
        _card = false;
        _title = "Nicht in deinem Diktat";
        _warningWords = string.Join(", ", words.Take(WarningMaxWords))
            + (words.Count > WarningMaxWords ? $" und {words.Count - WarningMaxWords} weitere" : "");
        _hint = (pasted ? "Eingefügt." : "In der Zwischenablage, mit Strg+V einfügen.") + " Vor dem Speichern prüfen.";
        _context = Context.None;
        _screenshot = false;
        _warningUntil = DateTime.UtcNow + WarningShown;
        Present();
    }

    public void HideOverlay()
    {
        _phase = Phase.Hidden;
        _fadeStart = null;
        _screenshot = false;
        _timer.Stop();
        Hide();
    }

    // ---------------- colours ----------------

    private static bool Hc => SystemInformation.HighContrast;
    private static Color White(int alpha) => Hc ? SystemColors.WindowText : Color.FromArgb(alpha, 255, 255, 255);
    private static Color CAmber => Hc ? SystemColors.WindowText : Amber;
    private static int A(double opacity) => (int)Math.Round(255 * opacity);

    // ---------------- layout ----------------

    private bool ShowsCard => _phase == Phase.Recording && _card;
    private bool HasCancel => _phase is Phase.Recording or Phase.Processing;
    private bool HasConfirm => _phase == Phase.Recording;

    private void EnsureFonts()
    {
        if (_label != null && _fontDpi == DeviceDpi) return;
        foreach (var f in new[] { _label, _labelRegular, _row, _small, _icon }) f?.Dispose();
        float k = S;
        _fontDpi = DeviceDpi;
        _label = new Font(SemiboldFace, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _labelRegular = new Font(TextFace, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _row = new Font(TextFace, 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _small = new Font(TextFace, 11f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _icon = new Font(IconFace, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private float MeasureWidth(Graphics g, string text, Font font) =>
        text.Length == 0 ? 0 : g.MeasureString(text, font, new SizeF(10000, 1000), _line).Width;

    /// <summary>The word next to the bars or the spinner, or the amber warning that replaces it.</summary>
    private (string Text, bool Warn) StatusLabel()
    {
        if (_phase != Phase.Recording) return (_title, false);
        if (_peakSoFar != null && Core.LevelMeter.IsSilent(_peakSoFar(), DateTime.UtcNow - _started, SilentAfter))
            return ("Kein Ton vom Mikrofon. Stumm geschaltet oder gesperrt?", true);
        var left = _limit - (DateTime.UtcNow - _started);
        if (left <= LimitWarning && left > TimeSpan.Zero)
            return ($"Endet in {Core.Clock.Format(left)} automatisch", true);
        return (_card ? FooterText() : _title, false);
    }

    /// <summary>Mac footerLabel: what stopping does. "↩" becomes the stop shortcut, shown dim at the right.</summary>
    private string FooterText()
    {
        if (_templateName == null) return "Sprich deine Anweisung";
        if (_context == Context.Selection)
            return $"Formatiert die Markierung ({Count(_selection)} Zeichen) als „{_templateName}“";
        return $"Formatiert es als „{_templateName}“";
    }

    private static string Count(string text) =>
        text.Length.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

    private int WarningLines(Graphics g, float cardWidth)
    {
        float h = g.MeasureString(_warningWords, _row!, new SizeF(cardWidth - (CardPad + 8) * 2 * S, 1000), _wrap).Height;
        return Math.Clamp((int)Math.Ceiling(h / (18f * S)), 1, 3);
    }

    private float PillContentWidth(Graphics g)
    {
        float s = S;
        var (text, _) = StatusLabel();
        float label = MeasureWidth(g, text, _label!);
        return _phase switch
        {
            Phase.Recording => Button + Gap + (_level != null ? BarsWidth + Gap : 0) + label / s + Gap + Button,
            Phase.Processing => Button + Gap + 14 + Gap + label / s + (_screenshot ? 4 + 14 : 0),
            _ => 20 + Gap + label / s, // Done
        };
    }

    private const float BarsWidth = Bars * 3 + (Bars - 1) * 2.5f;

    /// <summary>Size of the pill or card itself, without the shadow margin.</summary>
    private SizeF CardSize(Graphics g)
    {
        float s = S;
        if (_phase == Phase.Warning)
            return new SizeF(WarningW * s, (CardPad * 2 + HeaderH + WarningLines(g, WarningW * s) * 18 + 4 + 22 + 4) * s);
        if (ShowsCard)
        {
            int rows = _quick.Count;
            float h = CardPad * 2 + HeaderH + (_screenshot ? RowH : 0) + DividerH
                + rows * RowH + (rows > 0 ? DividerH : 0) + FooterH;
            return new SizeF(CardW * s, h * s);
        }
        float min = _phase == Phase.Recording ? MinRecordingPill : _phase == Phase.Processing ? MinProcessingPill : 0;
        float w = Math.Max(min, PillContentWidth(g) + PillPad * 2);
        return new SizeF(w * s, PillH * s);
    }

    private string LayoutKey() => $"{_phase}|{_card}|{StatusLabel().Text}|{_quick.Count}|{_screenshot}|{DeviceDpi}";

    // A monitor was plugged in, unplugged or rearranged: place the pill again without restarting its fade.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !Visible) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && Visible) Present(placementOnly: true); });
            else Present(placementOnly: true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    private void Present(bool placementOnly = false)
    {
        EnsureFonts();
        using var probe = LayeredWindow.NewBitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        ConfigureText(pg);
        var card = CardSize(pg);
        int m = (int)Math.Ceiling(ShadowMargin * S);
        var size = new Size((int)Math.Ceiling(card.Width) + 2 * m, (int)Math.Ceiling(card.Height) + 2 * m);
        // Mac: centered on the screen the user works on, its bottom 28 pt above the Dock (here the taskbar);
        // the card grows upwards from the same bottom edge.
        var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
        _basePos = new Point(area.Left + (area.Width - size.Width) / 2,
            area.Bottom - (int)Math.Round(BottomMargin * S) - (int)Math.Ceiling(card.Height) - m);
        if (Size != size || Location != _basePos) SetBounds(_basePos.X, _basePos.Y, size.Width, size.Height);
        _laidOutFor = LayoutKey();
        if (placementOnly)
        {
            if (Visible) Redraw();
            return;
        }

        _phaseStart = DateTime.UtcNow;
        _fadeStart = null;
        string? context = _context switch
        {
            Context.Reading => Core.UserCopy.OverlayReadingSelection,
            Context.Nothing => Core.UserCopy.OverlayNothingSelected,
            Context.Selection => $"Auswahl: {Count(_selection)} Zeichen",
            _ => null,
        };
        AccessibleDescription = Core.UserCopy.OverlayAnnouncement(_title, _hint, context)
            + (_phase == Phase.Recording ? $". ✕: {Core.UserCopy.OverlayDiscard}. ✓: Beenden und verarbeiten"
                : _phase == Phase.Processing ? $". ✕: {Core.UserCopy.OverlayCancelProcessing}" : "");
        if (!Visible)
        {
            _appearStart = DateTime.UtcNow;
            Show();
        }
        SetClickThrough(_phase == Phase.Done);
        _settled = false;
        _timer.Start();
        Redraw();
        Core.Log.Info($"Overlay: {_phase} at {Bounds}, visible {Visible}");
    }

    /// <summary>For the UI tests: one animation frame, as the 33 ms timer runs it.</summary>
    internal void TickForTests() => OnTick();

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        if (_fadeStart == null && ((_phase == Phase.Done && now >= _doneUntil) || (_phase == Phase.Warning && now >= _warningUntil)))
            _fadeStart = now;
        if (_fadeStart != null && (now - _fadeStart.Value).TotalSeconds >= Fade)
        {
            HideOverlay();
            return;
        }
        if (_phase == Phase.Recording && _level != null)
        {
            // Mac pushLevel: the bars are a short history, newest on the right.
            float v = _meter.Feed(_level());
            if (++_tick % TicksPerBar == 0)
            {
                Array.Copy(_levels, 1, _levels, 0, Bars - 1);
                _levels[Bars - 1] = v;
            }
            // Mac .animation(.easeOut(duration: 0.1)) on the bar heights.
            for (int i = 0; i < Bars; i++) _shown[i] += (_levels[i] - _shown[i]) * 0.55f;
        }
        // The warning or the near-limit countdown changes the label's width: resize around it.
        if (_phase is Phase.Recording && _fadeStart == null && LayoutKey() != _laidOutFor)
        {
            Present(placementOnly: true);
            return;
        }
        bool animating = _phase is Phase.Recording or Phase.Processing || _fadeStart != null
            || (now - _appearStart).TotalSeconds < Fade || (_phase == Phase.Done && (now - _phaseStart).TotalSeconds < DonePop);
        if (!animating && _settled) return;
        _settled = !animating;
        Redraw();
    }

    // ---------------- painting ----------------

    private static void ConfigureText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias; // unhinted like macOS; GridFit spread some word gaps
    }

    private static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0f, 1f), 3);

    private void Redraw()
    {
        if (!IsHandleCreated || !Visible || _phase == Phase.Hidden || Width <= 0 || Height <= 0) return;
        EnsureFonts();
        float s = S;
        var now = DateTime.UtcNow;
        float appear = Ease((float)(now - _appearStart).TotalSeconds / Fade);
        float fade = _fadeStart == null ? 1f : 1f - Ease((float)(now - _fadeStart.Value).TotalSeconds / Fade);

        using var bmp = RenderFrame(now);
        // Mac: alpha only, no slide.
        LayeredWindow.Update(Handle, bmp, _basePos, (byte)Math.Clamp(255 * appear * fade, 0, 255));
    }

    private Bitmap RenderFrame(DateTime now)
    {
        float s = S;
        var bmp = LayeredWindow.NewBitmap(Width, Height);
        using var g = Graphics.FromImage(bmp);
        ConfigureText(g);
        float m = ShadowMargin * s;
        PaintFrame(g, new RectangleF(m, m, Width - 2 * m, Height - 2 * m), s, now);
        return bmp;
    }

    /// <summary>For the UI tests and the screenshot comparison: the current frame as drawn, before the fade alpha.</summary>
    internal Bitmap RenderForTests()
    {
        EnsureFonts();
        return RenderFrame(DateTime.UtcNow);
    }

    private void PaintFrame(Graphics g, RectangleF card, float s, DateTime now)
    {
        bool pill = !ShowsCard && _phase != Phase.Warning;
        PaintBackground(g, card, pill ? card.Height / 2 : CardRadius * s, s, border: !pill);
        _cancelRect = _confirmRect = RectangleF.Empty;
        _rowRects.Clear();
        if (_phase == Phase.Warning) PaintWarning(g, card, s);
        else if (ShowsCard) PaintCard(g, card, s, now);
        else PaintPill(g, card, s, now);
    }

    /// <summary>Mac pillBody: one row, spacing 8, horizontal padding 8, centred in the capsule.</summary>
    private void PaintPill(Graphics g, RectangleF card, float s, DateTime now)
    {
        float cy = card.Y + card.Height / 2;
        var (text, warn) = StatusLabel();
        float content = PillContentWidth(g) * s;
        float x = card.X + Math.Max(PillPad * s, (card.Width - content) / 2);
        switch (_phase)
        {
            case Phase.Recording:
            {
                _cancelRect = new RectangleF(x, cy - Button / 2 * s, Button * s, Button * s);
                DrawCircleButton(g, _cancelRect, confirm: false, s);
                x += (Button + Gap) * s;
                if (_level != null)
                {
                    DrawBars(g, x, cy, s);
                    x += (BarsWidth + Gap) * s;
                }
                _confirmRect = new RectangleF(card.Right - Math.Max(PillPad * s, (card.Width - content) / 2) - Button * s,
                    cy - Button / 2 * s, Button * s, Button * s);
                DrawText(g, text, _label!, warn ? CAmber : White(255), x, cy, _confirmRect.X - Gap * s - x);
                DrawCircleButton(g, _confirmRect, confirm: true, s);
                break;
            }
            case Phase.Processing:
            {
                _cancelRect = new RectangleF(x, cy - Button / 2 * s, Button * s, Button * s);
                DrawCircleButton(g, _cancelRect, confirm: false, s);
                x += (Button + Gap) * s;
                DrawSpinner(g, new PointF(x + 7 * s, cy), 14 * s, White(A(0.9)), now);
                x += (14 + Gap) * s;
                float w = MeasureWidth(g, text, _label!);
                DrawText(g, text, _label!, White(255), x, cy, card.Right - PillPad * s - x);
                if (_screenshot)
                    DrawIcon(g, "", _icon!, White(A(0.6)), x + w + 4 * s, cy, 14 * s);
                break;
            }
            case Phase.Done:
                DrawDoneMark(g, new PointF(x + 10 * s, cy), s, now);
                DrawText(g, _title, _label!, White(255), x + (20 + Gap) * s, cy, card.Right - PillPad * s - x - (20 + Gap) * s);
                break;
        }
    }

    /// <summary>Mac QuickActionCardView: header, divider, rows, divider, footer.</summary>
    private void PaintCard(Graphics g, RectangleF card, float s, DateTime now)
    {
        float left = card.X + CardPad * s, right = card.Right - CardPad * s;
        float y = card.Y + CardPad * s;

        PaintHeader(g, new RectangleF(left, y, right - left, HeaderH * s), s, now);
        y += HeaderH * s;
        if (_screenshot)
        {
            float cy = y + RowH / 2 * s;
            DrawIcon(g, "", _icon!, White(A(0.6)), left + 8 * s, cy, 16 * s);
            DrawText(g, ScreenshotLine, _labelRegular!, White(A(0.55)), left + 32 * s, cy, right - left - 40 * s);
            y += RowH * s;
        }
        y = Divider(g, left, right, y, s);

        if (_quick.Count > 0)
        {
            bool nothing = _context == Context.Nothing;
            for (int i = 0; i < _quick.Count; i++)
            {
                var row = new RectangleF(left, y, right - left, RowH * s);
                _rowRects.Add(row);
                bool highlighted = i == _hover;
                bool muted = nothing && Core.QuickActionKinds.IsEdit(_quick[i]) && !highlighted;
                if (highlighted) FillRounded(g, row, RowRadius * s, Hc ? SystemColors.Highlight : White(A(0.16)));
                float cy = row.Y + row.Height / 2;
                DrawIcon(g, KindGlyph(Core.QuickActionKinds.Classify(_quick[i])), _icon!, White(A(muted ? 0.35 : 0.7)), row.X + 8 * s, cy, 16 * s);
                string key = i < 9 ? $"Strg+{i + 1}" : "";
                float keyW = MeasureWidth(g, key, _small!);
                DrawText(g, key, _small!, White(A(highlighted ? 0.8 : 0.35)), row.Right - 8 * s - keyW, cy, keyW + 2 * s);
                var textColor = highlighted && Hc ? SystemColors.HighlightText : White(A(muted ? 0.45 : 1));
                DrawText(g, _quick[i], _row!, textColor, row.X + 32 * s, cy, row.Right - 8 * s - keyW - 8 * s - (row.X + 32 * s));
                y += RowH * s;
            }
            y = Divider(g, left, right, y, s);
        }

        // Footer: ✕, bars, what stopping does, the stop shortcut (Mac: a dim ↩), ✓.
        float fy = y + FooterH / 2 * s;
        float x = left + 4 * s;
        _cancelRect = new RectangleF(x, fy - Button / 2 * s, Button * s, Button * s);
        DrawCircleButton(g, _cancelRect, confirm: false, s);
        x += (Button + Gap) * s;
        if (_level != null)
        {
            DrawBars(g, x, fy, s);
            x += (BarsWidth + Gap) * s;
        }
        _confirmRect = new RectangleF(right - 4 * s - Button * s, fy - Button / 2 * s, Button * s, Button * s);
        DrawCircleButton(g, _confirmRect, confirm: true, s);
        float keyRight = _confirmRect.X - Gap * s;
        float stopW = MeasureWidth(g, _stopKey, _small!);
        DrawText(g, _stopKey, _small!, White(A(0.4)), keyRight - stopW, fy, stopW + 2 * s);
        var (text, warn) = StatusLabel();
        DrawText(g, text, _label!, warn ? CAmber : White(255), x, fy, keyRight - stopW - Gap * s - x);
    }

    /// <summary>Mac contextHeader / templateHeader: what the run acts on.</summary>
    private void PaintHeader(Graphics g, RectangleF r, float s, DateTime now)
    {
        float cy = r.Y + r.Height / 2, iconX = r.X + 8 * s, textX = r.X + 32 * s, maxW = r.Right - 8 * s - textX;
        if (_templateName != null && _context != Context.Reading)
        {
            if (!Hc) FillRounded(g, r, RowRadius * s, Color.FromArgb(A(0.28), MacAccent));
            DrawIcon(g, "", _icon!, White(A(0.9)), iconX, cy, 16 * s);
            DrawText(g, _templateName, _label!, White(255), textX, cy, maxW);
            return;
        }
        switch (_context)
        {
            case Context.Reading:
                DrawSpinner(g, new PointF(iconX + 8 * s, cy), 10 * s, White(A(0.6)), now);
                DrawText(g, Core.UserCopy.OverlayReadingSelection, _labelRegular!, White(A(0.55)), textX, cy, maxW);
                break;
            case Context.Nothing:
                DrawIcon(g, "", _icon!, CAmber, iconX, cy, 16 * s);
                DrawText(g, Core.UserCopy.OverlayNothingSelected, _label!, CAmber, textX, cy, maxW);
                break;
            case Context.Selection:
            {
                DrawIcon(g, "", _icon!, White(A(0.6)), iconX, cy, 16 * s);
                string chars = $"{Count(_selection)} Zeichen";
                float cw = MeasureWidth(g, chars, _small!);
                DrawText(g, chars, _small!, White(A(0.45)), r.Right - 8 * s - cw, cy, cw + 2 * s);
                DrawText(g, $"Auswahl: „{Preview(_selection)}“", _labelRegular!, White(A(0.75)), textX, cy, maxW - cw - 8 * s);
                break;
            }
        }
    }

    /// <summary>First stretch of the selection on one line (Mac QuickActionCardView.preview).</summary>
    private static string Preview(string text) =>
        string.Join(" ", text[..Math.Min(200, text.Length)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void PaintWarning(Graphics g, RectangleF card, float s)
    {
        float left = card.X + CardPad * s, right = card.Right - CardPad * s;
        float y = card.Y + CardPad * s, cy = y + HeaderH / 2 * s;
        DrawIcon(g, "", _icon!, CAmber, left + 8 * s, cy, 16 * s);
        DrawText(g, _title, _label!, CAmber, left + 32 * s, cy, right - left - 40 * s);
        y += HeaderH * s;
        int lines = WarningLines(g, card.Width);
        using (var br = new SolidBrush(White(255)))
            g.DrawString(_warningWords, _row!, br, new RectangleF(left + 8 * s, y, right - left - 16 * s, lines * 18 * s), _wrap);
        y += (lines * 18 + 4) * s;
        DrawText(g, _hint, _small!, White(A(0.55)), left + 8 * s, y + 11 * s, right - left - 16 * s);
    }

    private float Divider(Graphics g, float left, float right, float y, float s)
    {
        float ly = y + 4 * s + 0.5f * s;
        using var pen = new Pen(Hc ? SystemColors.WindowFrame : White(A(0.1)), 1f * s);
        g.DrawLine(pen, left + 8 * s, ly, right - 8 * s, ly);
        return y + DividerH * s;
    }

    private static string KindGlyph(Core.QuickActionKind kind) => kind switch
    {
        Core.QuickActionKind.Correct => "",   // checkmark.circle
        Core.QuickActionKind.Rewrite => "",   // pencil
        Core.QuickActionKind.Shorten => "",   // arrow.down.right.and.arrow.up.left
        Core.QuickActionKind.Format => "",    // list.bullet
        Core.QuickActionKind.Translate => "", // globe
        _ => "",                              // text.bubble
    };

    /// <summary>Shadow (Mac hasShadow), fill and hairline border, drawn once per size and copied from there.</summary>
    private void PaintBackground(Graphics g, RectangleF card, float radius, float s, bool border)
    {
        var key = (Size, card, radius, Hc);
        if (_cardCache == null || _cardKey != key)
        {
            _cardCache?.Dispose();
            _cardCache = LayeredWindow.NewBitmap(Width, Height);
            _cardKey = key;
            using var cg = Graphics.FromImage(_cardCache);
            ConfigureText(cg);
            if (!Hc) LayeredWindow.Shadow(cg, card, radius, s);
            using (var path = Theme.Rounded(card, radius))
            {
                using var fill = new SolidBrush(Hc ? SystemColors.Window : Fill);
                cg.FillPath(fill, path);
                if (Hc)
                {
                    using var edge = new Pen(SystemColors.WindowFrame, 1.5f);
                    cg.DrawPath(edge, path);
                }
            }
            if (!Hc && border)
            {
                // Mac strokeBorder(Color.white.opacity(0.08)).
                var inner = RectangleF.Inflate(card, -0.5f * s, -0.5f * s);
                using var path = Theme.Rounded(inner, radius - 0.5f * s);
                using var pen = new Pen(Color.FromArgb(A(0.08), 255, 255, 255), 1f * s);
                cg.DrawPath(pen, path);
            }
        }
        var (mode, interpolation, offset) = (g.CompositingMode, g.InterpolationMode, g.PixelOffsetMode);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_cardCache, new Rectangle(0, 0, _cardCache.Width, _cardCache.Height), 0, 0, _cardCache.Width, _cardCache.Height, GraphicsUnit.Pixel);
        (g.CompositingMode, g.InterpolationMode, g.PixelOffsetMode) = (mode, interpolation, offset);
    }

    /// <summary>Mac PillCircleButton: ✕ white on Color(white: 0.28), ✓ black on white; 24 pt, bold 10 pt mark.</summary>
    private void DrawCircleButton(Graphics g, RectangleF r, bool confirm, float s)
    {
        bool hot = _hot == (confirm ? 2 : 1);
        Color disc = Hc ? SystemColors.ButtonFace
            : confirm ? (hot ? Color.FromArgb(222, 222, 222) : Color.White)
            : (hot ? Color.FromArgb(96, 96, 96) : Disc);
        using (var b = new SolidBrush(disc)) g.FillEllipse(b, r);
        if (Hc)
        {
            using var edge = new Pen(SystemColors.WindowFrame, 1f);
            g.DrawEllipse(edge, r);
        }
        var ink = Hc ? SystemColors.ControlText : confirm ? Color.Black : Color.White;
        using var mark = new Pen(ink, 1.9f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        if (confirm)
        {
            g.DrawLines(mark, new[]
            {
                new PointF(cx - 4f * s, cy + 0.2f * s),
                new PointF(cx - 1.3f * s, cy + 3f * s),
                new PointF(cx + 4f * s, cy - 2.9f * s),
            });
        }
        else
        {
            float k = 3.4f * s;
            g.DrawLine(mark, cx - k, cy - k, cx + k, cy + k);
            g.DrawLine(mark, cx + k, cy - k, cx - k, cy + k);
        }
    }

    /// <summary>Mac SpinnerView: a circle trimmed from 0.18 to 1, 2 pt, one turn per 0.9 s.</summary>
    private void DrawSpinner(Graphics g, PointF c, float size, Color color, DateTime now)
    {
        float r = size / 2 - S;
        float angle = (float)((now - _phaseStart).TotalSeconds / 0.9 * 360 % 360);
        using var pen = new Pen(color, 2f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, c.X - r, c.Y - r, r * 2, r * 2, angle + 0.18f * 360, 0.82f * 360);
    }

    private void DrawDoneMark(Graphics g, PointF c, float s, DateTime now)
    {
        float since = (float)(now - _phaseStart).TotalSeconds;
        float k = Math.Clamp(since / DonePop, 0f, 1f);
        float pop = 1 + 0.18f * MathF.Sin(k * MathF.PI) * (1 - k);
        float r = 10f * s * Ease(k * 1.4f) * pop;
        Color tone = Hc ? SystemColors.WindowText : _doneNeutral ? Color.FromArgb(142, 142, 152) : Green;
        using var circle = new SolidBrush(tone);
        g.FillEllipse(circle, c.X - r, c.Y - r, r * 2, r * 2);
        var ink = Hc ? SystemColors.Window : Color.Black;
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
    }

    /// <summary>Mac LevelBarsView: ten white capsules, 3 pt wide, 2.5 pt apart, 3–18 pt high.</summary>
    private void DrawBars(Graphics g, float x, float cy, float s)
    {
        float barW = 3f * s, gap = 2.5f * s, minH = 3f * s, maxH = 18f * s;
        using var brush = new SolidBrush(White(255));
        for (int i = 0; i < Bars; i++)
        {
            float h = minH + Math.Clamp(_shown[i], 0f, 1f) * (maxH - minH);
            using var path = RoundedBar(x + i * (barW + gap), cy - h / 2, barW, h);
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

    private static void FillRounded(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = Theme.Rounded(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    /// <summary>One line of text, vertically centred on <paramref name="cy"/>, cut off with an ellipsis at <paramref name="maxW"/>.</summary>
    private void DrawText(Graphics g, string text, Font font, Color color, float x, float cy, float maxW)
    {
        if (maxW <= 0 || text.Length == 0) return;
        float h = font.GetHeight(g);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(x, cy - h / 2, maxW, h + 2), _line);
    }

    /// <summary>An icon glyph centred in a <paramref name="width"/>-wide column starting at <paramref name="x"/>.</summary>
    private void DrawIcon(Graphics g, string glyph, Font font, Color color, float x, float cy, float width)
    {
        float w = MeasureWidth(g, glyph, font), h = font.GetHeight(g);
        using var brush = new SolidBrush(color);
        g.DrawString(glyph, font, brush, x + (width - w) / 2, cy - h / 2, _line);
    }

    // ---------------- mouse ----------------

    private int HitButton(Point p) =>
        HasCancel && _cancelRect.Contains(p) ? 1 : HasConfirm && _confirmRect.Contains(p) ? 2 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = ShowsCard ? _rowRects.FindIndex(r => r.Contains(e.Location)) : -1;
        int hot = HitButton(e.Location);
        Cursor = hover >= 0 || hot != 0 || _phase == Phase.Warning ? Cursors.Hand : Cursors.Default;
        if (hover == _hover && hot == _hot) return;
        _hover = hover;
        _hot = hot;
        Redraw();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0 && _hot == 0) return;
        _hover = -1;
        _hot = 0;
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
        switch (HitButton(e.Location))
        {
            case 1: CancelClicked?.Invoke(); return;
            case 2: ConfirmClicked?.Invoke(); return;
        }
        if (!ShowsCard) return;
        int row = _rowRects.FindIndex(r => r.Contains(e.Location));
        if (row >= 0) QuickActionClicked?.Invoke(row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _timer.Dispose();
            _cardCache?.Dispose();
            foreach (var f in new[] { _label, _labelRegular, _row, _small, _icon }) f?.Dispose();
            _line.Dispose();
            _wrap.Dispose();
        }
        base.Dispose(disposing);
    }
}
