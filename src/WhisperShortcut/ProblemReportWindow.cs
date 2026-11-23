namespace WhisperShortcut;

/// <summary>
/// The one „Problem melden“ window (Mac #549 DictationReportWindow). The tray, Einstellungen → Über, an error popup, the
/// chat's <c>/feedback</c> and an MCP <c>report_problem</c> all open it. It starts on the newest result (dictation, Dictate
/// Prompt answer, Read Aloud; held in memory only), or on general feedback. The user writes what went wrong, may open and
/// edit the details, and sends by e-mail: the report travels as a .txt file (message, details, technical lines,
/// configuration and the end of the log), attached by the mail program or, where that is not possible, selected in
/// Explorer and copied for Strg+V. Nothing leaves the PC until the user presses send in the mail program.
/// Windows differences: „Im Explorer zeigen“ instead of the Mac share sheet; the configuration file also carries the
/// last log lines (Windows has no hang reports, the log is what explains a failure).
/// </summary>
internal sealed class ProblemReportWindow : Form
{
    /// <summary>Set by TrayApp at start: the results the window offers, newest first, general feedback last.</summary>
    internal static Func<IReadOnlyList<Core.ResultCandidate>>? CandidatesProvider;

    /// <summary>Set by TrayApp at start: the configuration section of the report file (settings, prompts, glossary, templates, log tail).</summary>
    internal static Func<string>? ConfigurationProvider;

    private static ProblemReportWindow? _current;

    private readonly IReadOnlyList<Core.ResultCandidate> _candidates;
    private readonly Dictionary<string, string> _editedDetails = new();
    private readonly string _openedMessage;
    private readonly bool _activate;
    private Action<string>? _onHandedOff;
    private bool _handedOff, _switching;
    private string _selected;

    private readonly FluentCombo _about;
    private readonly ThemedLabel _messageLabel, _privacy, _fileNote;
    private readonly TextBox _message, _details;
    private readonly FieldFrame _detailsFrame;
    private readonly LinkLabel _detailsToggle, _preview;
    private readonly ToggleSwitch _includeConfiguration;
    private readonly FluentButton _copy, _reveal, _send, _done;
    private readonly Font _mono = new("Consolas", 9.5f);

    /// <summary>True while a report window is on screen.</summary>
    public static bool IsShowing => _current is { IsDisposed: false };

    /// <summary>
    /// Opens the window (Mac ProblemReport.open). <paramref name="message"/> prefills „Was ist schiefgelaufen?“;
    /// <paramref name="details"/> goes above the general candidate's lines (the chat tail from /feedback);
    /// <paramref name="activate"/> false keeps the keyboard where it is (an MCP report while the user types);
    /// <paramref name="onHandedOff"/> runs once, with the report text, when the user first sends, shows or copies it;
    /// <paramref name="asDraft"/> makes the prefilled text count as the user's draft (an AI app's report), so a later open
    /// does not replace it. An edited draft on screen is kept; an untouched window is replaced.
    /// </summary>
    public static void Open(string? message = null, string? details = null, bool startOnGeneral = false,
        bool activate = true, Action<string>? onHandedOff = null, bool asDraft = false)
    {
        var provided = CandidatesProvider?.Invoke() ?? Array.Empty<Core.ResultCandidate>();
        if (provided.Count == 0)
            provided = Core.ResultReport.Candidates(Array.Empty<Core.DictationResult>(), Array.Empty<string>(), false,
                null, null, null, DateTime.Now);
        var candidates = Core.ResultReport.ApplyingExtraDetails(details, provided);

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
        var window = new ProblemReportWindow(candidates, message ?? "", asDraft ? "" : message ?? "", startOnGeneral, activate, onHandedOff);
        _current = window;
        window.FormClosed += (_, _) => { if (_current == window) _current = null; };
        window.Present(activate);
        Core.Log.Info($"REPORT: opened on {window.Current?.Kind} ({candidates.Count} results)");
    }

    private ProblemReportWindow(IReadOnlyList<Core.ResultCandidate> candidates, string message, string openedMessage,
        bool startOnGeneral, bool activate, Action<string>? onHandedOff)
    {
        _candidates = candidates;
        _openedMessage = openedMessage;
        _activate = activate;
        _onHandedOff = onHandedOff;
        _selected = Core.ResultReport.InitialSelection(candidates, startOnGeneral);

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

        const int Width = 660;
        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 20, 24, 16), Margin = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Width));
        void Add(Control c) { root.Controls.Add(c); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
        ThemedLabel Heading(string text) => new()
        {
            Text = text, Font = Theme.Strong, Tone = Tone.Secondary, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
        };

        Add(Heading("Worum geht es?"));
        _about = new FluentCombo { Width = Width, AccessibleName = "Worum geht es?", Margin = new Padding(0, 0, 0, 14) };
        foreach (var c in candidates) _about.Items.Add(Core.ResultReport.MenuTitle(c));
        _about.SelectedIndex = Math.Max(0, IndexOf(_selected));
        _about.SelectedIndexChanged += (_, _) => SwitchTo(_candidates[_about.SelectedIndex].Id);
        Add(_about);

        _messageLabel = Heading("");
        Add(_messageLabel);
        _message = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Text = message };
        _message.TextChanged += (_, _) => Edited();
        Add(new FieldFrame(_message, multiline: true) { Width = Width, Height = 84, Margin = new Padding(0, 0, 0, 12) });

        // Folded: the recorded facts matter to the developer. Open, they can be read and edited (a patient name).
        _detailsToggle = new LinkLabel { AutoSize = true, Margin = new Padding(0, 0, 0, 4), AccessibleName = "Details ansehen und bearbeiten" };
        _detailsToggle.LinkClicked += (_, _) => ShowDetails(!_detailsFrame!.Visible);
        Add(_detailsToggle);
        _details = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Font = _mono, AccessibleName = "Details",
        };
        _details.TextChanged += (_, _) =>
        {
            if (_switching) return;
            _editedDetails[_selected] = _details.Text.Replace("\r\n", "\n");
            Edited();
        };
        _detailsFrame = new FieldFrame(_details, multiline: true) { Width = Width, Height = 200, Margin = new Padding(0, 2, 0, 12), Visible = false };
        Add(_detailsFrame);

        // On by default: without the configuration a report about a wrong word cannot be reproduced.
        var configLine = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Width = Width, Margin = new Padding(0, 0, 0, 10) };
        configLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        configLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _includeConfiguration = new ToggleSwitch
        {
            Checked = true, Margin = new Padding(0, 0, 10, 0),
            AccessibleName = "Konfiguration mitsenden (Glossar, Anweisungen, Vorlagen, Einstellungen, Protokoll)",
        };
        _includeConfiguration.CheckedChanged += (_, _) => { UpdateTexts(); _copy!.Text = "Kopieren"; };
        configLine.Controls.Add(_includeConfiguration, 0, 0);
        configLine.Controls.Add(new ThemedLabel
        {
            Text = "Konfiguration mitsenden (Glossar, Anweisungen, Vorlagen, Einstellungen, Protokoll)",
            AutoSize = true, MaximumSize = new Size(Width - 160, 0), Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 0),
        }, 1, 0);
        _preview = new LinkLabel { Text = "Bericht ansehen", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 0, 0) };
        _preview.LinkClicked += (_, _) => Preview();
        configLine.Controls.Add(_preview, 2, 0);
        Add(configLine);

        _privacy = new ThemedLabel { Tone = Tone.Secondary, AutoSize = true, MaximumSize = new Size(Width, 0), Margin = new Padding(0, 0, 0, 10) };
        Add(_privacy);
        _fileNote = new ThemedLabel
        {
            Text = "Die Datei ist im Explorer markiert und kopiert. Drücke in der E-Mail Strg+V oder ziehe die Datei hinein.",
            Font = Theme.Strong, AutoSize = true, MaximumSize = new Size(Width, 0), Margin = new Padding(0, 0, 0, 10), Visible = false,
        };
        Add(_fileNote);

        var cancel = new FluentButton("Abbrechen") { MinimumSize = new Size(100, 32), AccessibleName = "Abbrechen" };
        cancel.Click += (_, _) => Close();
        _copy = new FluentButton("Kopieren") { MinimumSize = new Size(100, 32), AccessibleName = "Bericht kopieren" };
        _copy.Click += (_, _) => Copy();
        var reveal = _reveal = new FluentButton("Im Explorer zeigen") { MinimumSize = new Size(150, 32), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Berichtsdatei im Explorer zeigen" };
        reveal.Click += (_, _) => RevealFile();
        // No AcceptButton: Return in the message field must not send.
        _send = new FluentButton("Per E-Mail senden", accent: true) { MinimumSize = new Size(150, 32), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Per E-Mail senden" };
        _send.Click += (_, _) => SendEmail();
        _done = new FluentButton("Fertig", accent: true) { MinimumSize = new Size(150, 32), Margin = new Padding(8, 0, 0, 0), Visible = false, AccessibleName = "Fertig" };
        _done.Click += (_, _) => Close();

        var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = Width, Margin = new Padding(0, 6, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(cancel, 0, 0);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right };
        right.Controls.Add(_copy);
        right.Controls.Add(reveal);
        right.Controls.Add(_send);
        right.Controls.Add(_done);
        buttons.Controls.Add(right, 1, 0);
        Add(buttons);

        Controls.Add(root);
        CancelButton = cancel;
        ResumeLayout(true);

        ShowDetails(false);
        SwitchTo(_selected);
        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => { if (_activate) _message.Focus(); };
    }

    protected override bool ShowWithoutActivation => !_activate;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mono.Dispose();
        base.Dispose(disposing);
    }

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

    private int IndexOf(string id)
    {
        for (int i = 0; i < _candidates.Count; i++) if (_candidates[i].Id == id) return i;
        return -1;
    }

    private Core.ResultCandidate? Current => IndexOf(_selected) is var i and >= 0 ? _candidates[i] : null;
    private Core.ResultKind Kind => Current?.Kind ?? Core.ResultKind.General;
    private string Details => _editedDetails.TryGetValue(_selected, out var edited) ? edited : Current?.Details ?? "";

    /// <summary>The message differs from what the window opened with, or some details were edited.</summary>
    private bool IsEdited =>
        _message.Text != _openedMessage
        || _editedDetails.Any(kv => kv.Value != (_candidates.FirstOrDefault(c => c.Id == kv.Key)?.Details ?? ""));

    private void SwitchTo(string id)
    {
        _selected = id;
        _switching = true;
        _details.Text = Details.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _switching = false;
        _copy.Text = "Kopieren";
        UpdateTexts();
        Core.Log.Info($"REPORT: switched to {Kind}");
    }

    private void UpdateTexts()
    {
        _messageLabel.Text = Core.ResultReport.MessageLabel(Kind);
        _message.PlaceholderText = Core.ResultReport.Placeholder(Kind);
        _message.AccessibleName = _messageLabel.Text;
        _privacy.Text = "✋  " + Core.ResultReport.PrivacyNote(Kind, _includeConfiguration.Checked);
        _preview.Visible = _includeConfiguration.Checked;
    }

    private void ShowDetails(bool show)
    {
        SuspendLayout();
        _detailsFrame.Visible = show;
        _detailsToggle.Text = (show ? "▾  " : "▸  ") + "Details ansehen und bearbeiten";
        ResumeLayout(true);
    }

    private void Edited() => _copy.Text = "Kopieren";

    /// <summary>The user's sentence first, then the (edited) details (Mac DictationReportView.context).</summary>
    private string Context => Core.ResultReport.Context(Kind, _message.Text.Replace("\r\n", "\n"), Details);

    private static string AppVersion => UpdateService.CurrentVersion;
    private static string WindowsVersion => Environment.OSVersion.Version.ToString();

    /// <summary>What „Per E-Mail senden“ attaches, „Bericht ansehen“ opens and „Kopieren“ copies.</summary>
    private string ReportFileText()
    {
        string? configuration = null;
        if (_includeConfiguration.Checked)
        {
            try { configuration = ConfigurationProvider?.Invoke(); }
            catch (Exception e) { Core.Log.Warn($"REPORT: configuration not built ({e.GetType().Name})"); }
        }
        return Core.ResultReport.FileText(Core.FeedbackLinks.MessageBody(Context, AppVersion, WindowsVersion),
            Current?.Technical ?? "", configuration);
    }

    private string? WriteFile(string text)
    {
        try { return Core.ReportAttachment.Write(text, DateTime.Now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn($"REPORT: file not written ({e.GetType().Name})");
            ChoiceDialog.Ask(this, "Die Berichtsdatei konnte nicht erstellt werden.",
                "Nutze stattdessen „Kopieren“: Damit landet alles in der Zwischenablage.", new[] { "OK" });
            return null;
        }
    }

    private void HandedOff(string body)
    {
        if (_handedOff) return;
        _handedOff = true;
        _onHandedOff?.Invoke(body);
    }

    /// <summary>
    /// Mac sendEmail: without the configuration only the message goes, as a mailto: link. With it, the report file is
    /// attached by the mail program (Simple MAPI); otherwise mailto: opens, the file is selected in Explorer and on the
    /// clipboard, and the window stays open and says how to add it.
    /// </summary>
    private async void SendEmail()
    {
        var context = Context;
        if (!_includeConfiguration.Checked)
        {
            if (ReportSender.OpenMailto(Core.FeedbackLinks.MailtoUrlWithin(context, AppVersion, WindowsVersion)))
            {
                Core.Log.Info("REPORT: opened mailto with the message");
                HandedOff(Core.FeedbackLinks.MessageBody(context, AppVersion, WindowsVersion));
                Close();
            }
            else NoMailProgram();
            return;
        }

        var text = ReportFileText();
        if (WriteFile(text) is not { } file) return;
        var ui = SynchronizationContext.Current;
        var body = Core.FeedbackLinks.MessageBody(Core.FeedbackLinks.Truncated(context, Core.FeedbackLinks.MailtoContextLimit), AppVersion, WindowsVersion);
        SetBusy(true);
        ReportSender.ComposeOutcome outcome;
        try
        {
            // Never blocks the UI thread (the keyboard hook lives there). A MAPI error after the wait still falls back.
            outcome = await ReportSender.ComposeWithAttachmentAsync(Core.FeedbackLinks.EmailSubject(AppVersion), body, file,
                lateFailure: () => ui?.Post(_ => FallBack(this, file, context), null));
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
                Core.Log.Info($"REPORT: e-mail with {Path.GetFileName(file)} attached");
                Close();
                break;
            case ReportSender.ComposeOutcome.Pending:
                // Classic Outlook keeps the call open while its window waits. Stay open: should it still fail, the
                // fallback shows here instead of the user's text vanishing.
                ShowDone("Die E-Mail ist in deinem E-Mail-Programm geöffnet, mit dem Bericht im Anhang. Prüfe sie und sende sie dort ab.");
                break;
            default:
                FallBack(this, file, context);
                break;
        }
    }

    /// <summary>
    /// Mac without Apple Mail: mailto: with the message (the file name first), the file selected in Explorer and on the
    /// clipboard. Runs on the UI thread, also after the window closed (a late MAPI error); then only the mail opens.
    /// </summary>
    private static void FallBack(ProblemReportWindow window, string file, string context)
    {
        ReportSender.RevealInExplorer(file);
        ReportSender.CopyFileToClipboard(file);
        var withNote = Core.ResultReport.WithFileNote(context, Path.GetFileName(file));
        bool opened = ReportSender.OpenMailto(Core.FeedbackLinks.MailtoUrlWithin(withNote, AppVersion, WindowsVersion));
        Core.Log.Info($"REPORT: mailto {(opened ? "opened" : "did not open")}, {Path.GetFileName(file)} shown in Explorer");
        if (window.IsDisposed) return;
        window.ShowDone(opened
            ? "Die Datei ist im Explorer markiert und kopiert. Drücke in der E-Mail Strg+V oder ziehe die Datei hinein."
            : $"Kein E-Mail-Programm gefunden. Die Datei ist im Explorer markiert und kopiert: Schreib an {Core.FeedbackLinks.SupportEmail} und hänge sie an.");
    }

    private void ShowDone(string note)
    {
        _fileNote.Text = note;
        _fileNote.Visible = true;
        _send.Visible = false;
        _done.Visible = true;
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _send.Enabled = _copy.Enabled = _reveal.Enabled = !busy;
    }

    private void NoMailProgram() =>
        ChoiceDialog.Ask(this, "Kein E-Mail-Programm gefunden",
            $"Nutze „Kopieren“ und schick den Text an {Core.FeedbackLinks.SupportEmail}.", new[] { "OK" });

    /// <summary>Copy takes everything, configuration included: the one route that works from Citrix or a remote Outlook.</summary>
    private void Copy()
    {
        var text = ReportFileText();
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            _copy.Text = "Kopiert";
            HandedOff(text);
            Core.Log.Info($"REPORT: copied ({text.Length} chars)");
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: copy failed ({e.GetType().Name})");
        }
    }

    /// <summary>Windows stand-in for the Mac share sheet: the file selected in Explorer and on the clipboard.</summary>
    private void RevealFile()
    {
        var text = ReportFileText();
        if (WriteFile(text) is not { } file) return;
        ReportSender.RevealInExplorer(file);
        ReportSender.CopyFileToClipboard(file);
        HandedOff(text);
        Core.Log.Info($"REPORT: {Path.GetFileName(file)} shown in Explorer");
    }

    /// <summary>„Bericht ansehen“: the same file the send button would attach, opened in the text editor.</summary>
    private void Preview()
    {
        if (WriteFile(ReportFileText()) is not { } file) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true });
            Core.Log.Info($"REPORT: opened {Path.GetFileName(file)} for review");
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: preview did not open ({e.GetType().Name})");
        }
    }
}
