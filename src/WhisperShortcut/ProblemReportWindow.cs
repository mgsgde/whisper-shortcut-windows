namespace WhisperShortcut;

/// <summary>
/// The one „Problem melden“ window. The tray, Einstellungen → Über, an error popup, the chat's <c>/feedback</c> and an
/// MCP <c>report_problem</c> all open it. It is one text field: the user's message on top, below it everything the
/// report carries along (the newest result — dictation, Dictate Prompt answer, Read Aloud; held in memory only — then
/// settings, prompts, glossary, templates and the end of the log), keys already redacted. What stays in the field is
/// exactly what is sent; the user deletes what should not go (a patient name). „Per E-Mail senden“ puts the text into a
/// compose window (Simple MAPI) or, where that is not possible, on the clipboard with a mail that says to paste it.
/// Nothing leaves the PC until the user presses send in the mail program.
/// Deliberately simpler than the Mac's DictationReportWindow (no result picker, no details fold, no configuration
/// switch, no report file); see docs/macos-paritaet.md.
/// </summary>
internal sealed class ProblemReportWindow : Form
{
    /// <summary>Set by TrayApp at start: the results the report can be about, newest first, general feedback last.</summary>
    internal static Func<IReadOnlyList<Core.ResultCandidate>>? CandidatesProvider;

    /// <summary>Set by TrayApp at start: the configuration section (settings, prompts, glossary, templates, log tail).</summary>
    internal static Func<string>? ConfigurationProvider;

    private static ProblemReportWindow? _current;

    private readonly string _openedText;
    private readonly bool _activate;
    private readonly int _cursor;
    private Action<string>? _onHandedOff;
    private bool _handedOff;

    private readonly TextBox _text;
    private readonly ThemedLabel _note;
    private readonly FluentButton _copy, _send, _done;

    /// <summary>True while a report window is on screen.</summary>
    public static bool IsShowing => _current is { IsDisposed: false };

    /// <summary>
    /// Opens the window (Mac ProblemReport.open). <paramref name="message"/> goes on top of the text;
    /// <paramref name="details"/> becomes the „Kontext“ section of general feedback (the chat tail from /feedback);
    /// <paramref name="startOnGeneral"/> leaves out the newest result; <paramref name="activate"/> false keeps the
    /// keyboard where it is (an MCP report while the user types); <paramref name="onHandedOff"/> runs once, with the
    /// report text, when the user first sends or copies it; <paramref name="asDraft"/> makes the prefilled text count as
    /// the user's draft (an AI app's report), so a later open does not replace it. An edited draft on screen is kept;
    /// an untouched window is replaced.
    /// </summary>
    public static void Open(string? message = null, string? details = null, bool startOnGeneral = false,
        bool activate = true, Action<string>? onHandedOff = null, bool asDraft = false)
    {
        var provided = CandidatesProvider?.Invoke() ?? Array.Empty<Core.ResultCandidate>();
        if (provided.Count == 0)
            provided = Core.ResultReport.Candidates(Array.Empty<Core.DictationResult>(), Array.Empty<string>(), false,
                null, null, null, DateTime.Now);
        var candidates = Core.ResultReport.ApplyingExtraDetails(details, provided);
        var selected = Core.ResultReport.InitialSelection(candidates, startOnGeneral);
        var result = candidates.FirstOrDefault(c => c.Id == selected);

        if (_current is { IsDisposed: false } open)
        {
            if (open.IsEdited)
            {
                Core.Log.Info("REPORT: kept the open draft");
                open.Present(activate);
                return;
            }
            open.Close();
        }
        var configuration = Configuration();
        var text = Core.ResultReport.Draft(message, result, configuration);
        var opened = asDraft ? Core.ResultReport.Draft(null, result, configuration) : text;
        var window = new ProblemReportWindow(text, opened, (message ?? "").Trim().Replace("\r\n", "\n").Replace("\n", "\r\n").Length, activate, onHandedOff);
        _current = window;
        window.FormClosed += (_, _) => { if (_current == window) _current = null; };
        window.Present(activate);
        Core.Log.Info($"REPORT: opened on {result?.Kind} ({text.Length} chars)");
    }

    /// <summary>The configuration section, or at least the versions when it cannot be built.</summary>
    private static string Configuration()
    {
        try
        {
            if (ConfigurationProvider?.Invoke() is { Length: > 0 } configuration) return configuration;
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: configuration not built ({e.GetType().Name})");
        }
        return $"App {AppVersion} · Windows {Environment.OSVersion.Version}";
    }

    private ProblemReportWindow(string text, string openedText, int cursor, bool activate, Action<string>? onHandedOff)
    {
        _openedText = Normalized(openedText);
        _activate = activate;
        _cursor = cursor;
        _onHandedOff = onHandedOff;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Problem melden";
        Font = Theme.Body;
        Icon = TrayIcons.App;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true; // it outlives the settings window and must be findable behind the mail program
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        const int Width = 680;
        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 20, 24, 16), Margin = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Width));
        void Add(Control c) { root.Controls.Add(c); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }

        Add(new ThemedLabel
        {
            Text = "Schreib oben deine Nachricht. Darunter steht alles, was mitgeschickt wird – du kannst es ändern oder löschen.",
            AutoSize = true, MaximumSize = new Size(Width, 0), Margin = new Padding(0, 0, 0, 8),
        });

        _text = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), AccessibleName = "Nachricht und Bericht",
        };
        _text.TextChanged += (_, _) => _copy!.Text = "Kopieren";
        Add(new FieldFrame(_text, multiline: true) { Width = Width, Height = 400, Margin = new Padding(0, 0, 0, 10) });

        Add(new ThemedLabel
        {
            Text = "Es geht erst etwas raus, wenn du die E-Mail in deinem E-Mail-Programm absendest.",
            Tone = Tone.Secondary, AutoSize = true, MaximumSize = new Size(Width, 0), Margin = new Padding(0, 0, 0, 10),
        });
        _note = new ThemedLabel
        {
            Font = Theme.Strong, AutoSize = true, MaximumSize = new Size(Width, 0), Margin = new Padding(0, 0, 0, 10), Visible = false,
        };
        Add(_note);

        _copy = new FluentButton("Kopieren") { MinimumSize = new Size(100, 32), AccessibleName = "Bericht kopieren" };
        _copy.Click += (_, _) => Copy();
        var cancel = new FluentButton("Abbrechen") { MinimumSize = new Size(100, 32), AccessibleName = "Abbrechen" };
        cancel.Click += (_, _) => Close();
        // No AcceptButton: Return in the text must not send.
        _send = new FluentButton("Per E-Mail senden", accent: true) { MinimumSize = new Size(150, 32), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Per E-Mail senden" };
        _send.Click += (_, _) => SendEmail();
        _done = new FluentButton("Fertig", accent: true) { MinimumSize = new Size(150, 32), Margin = new Padding(8, 0, 0, 0), Visible = false, AccessibleName = "Fertig" };
        _done.Click += (_, _) => Close();

        var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = Width, Margin = new Padding(0, 6, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_copy, 0, 0);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right };
        right.Controls.Add(cancel);
        right.Controls.Add(_send);
        right.Controls.Add(_done);
        buttons.Controls.Add(right, 1, 0);
        Add(buttons);

        Controls.Add(root);
        CancelButton = cancel;
        ResumeLayout(true);

        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) =>
        {
            // The cursor where the message goes, the view at the top.
            _text.Select(Math.Min(_cursor, _text.TextLength), 0);
            _text.ScrollToCaret();
            if (_activate) _text.Focus();
        };
    }

    protected override bool ShowWithoutActivation => !_activate;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    private void Present(bool activate)
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (activate) { Activate(); BringToFront(); }
    }

    private static string Normalized(string s) => s.Replace("\r\n", "\n");

    /// <summary>The text differs from what the window opened with.</summary>
    private bool IsEdited => Normalized(_text.Text) != _openedText;

    private static string AppVersion => UpdateService.CurrentVersion;

    /// <summary>What is sent and copied: the field as it stands, keys redacted once more (one pasted into the message).</summary>
    private string ReportText() => Core.SecretRedactor.Redact(Normalized(_text.Text).Trim());

    private void HandedOff(string body)
    {
        if (_handedOff) return;
        _handedOff = true;
        _onHandedOff?.Invoke(body);
    }

    /// <summary>
    /// A compose window with the whole text when the mail program takes it (Simple MAPI); otherwise the text on the
    /// clipboard and a mailto: mail that says to paste it, and the window stays open and says so.
    /// </summary>
    private async void SendEmail()
    {
        var text = ReportText();
        var ui = SynchronizationContext.Current;
        SetBusy(true);
        ReportSender.ComposeOutcome outcome;
        try
        {
            // Never blocks the UI thread (the keyboard hook lives there). A MAPI error after the wait still falls back.
            outcome = await ReportSender.ComposeAsync(Core.FeedbackLinks.EmailSubject(AppVersion), text,
                lateFailure: () => ui?.Post(_ => FallBack(this, text), null));
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: compose failed ({e.GetType().Name})");
            outcome = ReportSender.ComposeOutcome.NotPossible;
        }
        if (IsDisposed) return;
        SetBusy(false);
        HandedOff(text);
        switch (outcome)
        {
            case ReportSender.ComposeOutcome.Open:
                Core.Log.Info($"REPORT: e-mail composed ({text.Length} chars)");
                Close();
                break;
            case ReportSender.ComposeOutcome.Pending:
                // Classic Outlook keeps the call open while its window waits. Stay open: should it still fail, the
                // fallback shows here instead of the user's text vanishing.
                ShowDone("Die E-Mail ist in deinem E-Mail-Programm geöffnet. Prüfe sie und sende sie dort ab.");
                break;
            default:
                FallBack(this, text);
                break;
        }
    }

    /// <summary>
    /// The new Outlook, Gmail in the browser, no mail program: the text on the clipboard, then the mail. Runs on the UI
    /// thread, also after the window closed (a late MAPI error).
    /// </summary>
    private static void FallBack(ProblemReportWindow window, string text)
    {
        bool copied = ReportSender.CopyText(text);
        bool opened = ReportSender.OpenMailto(Core.FeedbackLinks.MailtoUrl(AppVersion));
        Core.Log.Info($"REPORT: mailto {(opened ? "opened" : "did not open")}, report {(copied ? "copied" : "not copied")} ({text.Length} chars)");
        if (window.IsDisposed) return;
        window.ShowDone(!copied
            ? $"Der Bericht ließ sich nicht kopieren. Markiere den Text oben (Strg+A), kopiere ihn (Strg+C) und schick ihn an {Core.FeedbackLinks.SupportEmail}."
            : opened
                ? "Die E-Mail wird geöffnet. Klick in den Text der E-Mail und drück Strg+V – dann steht der Bericht drin. Danach dort absenden."
                : $"Kein E-Mail-Programm gefunden. Der Bericht ist kopiert: Schreib an {Core.FeedbackLinks.SupportEmail} und füge ihn mit Strg+V ein.");
    }

    private void ShowDone(string note)
    {
        _note.Text = note;
        _note.Visible = true;
        _send.Visible = false;
        _done.Visible = true;
        _done.Focus();
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _send.Enabled = _copy.Enabled = !busy;
    }

    /// <summary>The route that works from Citrix, a remote Outlook or a chat.</summary>
    private void Copy()
    {
        var text = ReportText();
        if (!ReportSender.CopyText(text)) return;
        _copy.Text = "Kopiert";
        HandedOff(text);
        Core.Log.Info($"REPORT: copied ({text.Length} chars)");
    }
}
