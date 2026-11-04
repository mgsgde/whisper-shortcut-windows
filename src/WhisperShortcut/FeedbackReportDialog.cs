namespace WhisperShortcut;

/// <summary>
/// „Meine Einstellungen senden“ (Mac #344 FeedbackReportSheet): shows exactly what would be shared, lets the user cut
/// anything out, and hands the text to their own WhatsApp or mail. Nothing is sent from here.
/// Privacy in three layers, as on the Mac: the base text (<see cref="Core.ConfigurationReport"/>) holds no text the user
/// wrote and never a key; glossary and instructions are opt-in switches, off by default; the preview is editable and
/// what is in it when a button is pressed is what is shared. Once the preview was edited the switches lock, because a
/// rebuild would bring back a line the user just deleted; „Neu beginnen“ is the way back. The note field is separate
/// for that reason: describing the problem must not lock the switches.
/// </summary>
internal sealed class FeedbackReportDialog : Form
{
    private readonly Func<Core.ConfigurationReportOptions, string> _build;
    private readonly TextBox _note, _draft;
    private readonly ToggleSwitch _glossary, _prompts;
    private readonly ThemedLabel _lockHint, _tooLong;
    private readonly FluentButton _startOver, _copy;
    private bool _edited, _rebuilding;

    /// <summary>Opens the dialog over <paramref name="owner"/> (null: centered on screen, from the tray).</summary>
    public static void Open(IWin32Window? owner, Core.Settings settings, Core.Mcp.IMcpHost? host)
    {
        // The MCP catalog reads effective values (defaults included) and holds no keys; without a host (UI tests) the
        // report still has the header and the instruction overview.
        var catalog = host != null ? Core.Mcp.McpSettingsCatalog.Build(settings, host) : Array.Empty<Core.Mcp.McpSetting>();
        var env = new Core.ConfigurationReportEnvironment(UpdateService.CurrentVersion, Environment.OSVersion.Version.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        using var dialog = new FeedbackReportDialog(o => Core.ConfigurationReport.Build(o, settings, catalog, env));
        Core.Log.Info("Send my settings opened");
        if (owner != null) dialog.ShowDialog(owner);
        else
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.ShowDialog();
        }
    }

    private FeedbackReportDialog(Func<Core.ConfigurationReportOptions, string> build)
    {
        _build = build;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Meine Einstellungen senden";
        Font = Theme.Body;
        Icon = TrayIcons.App;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 720);
        MinimumSize = new Size(560, 560);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 20, 24, 16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, bool fill = false)
        {
            root.Controls.Add(c);
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        }
        ThemedLabel Hint(string text, int bottom = 8) => new()
        {
            Text = text, Tone = Tone.Secondary, AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 0, 0, bottom),
        };

        Add(new ThemedLabel { Text = "Meine Einstellungen senden", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        Add(Hint("Nichts verlässt deinen PC automatisch. „Senden“ öffnet dein eigenes WhatsApp oder E-Mail-Programm mit diesem Text, "
            + "abgeschickt wird erst dort, von dir. Lösche unten alles, was du nicht teilen möchtest.", 12));

        _note = new TextBox { Multiline = true, AcceptsReturn = true, PlaceholderText = "Was ist passiert? (optional)", AccessibleName = "Was ist passiert? (optional)" };
        Add(new FieldFrame(_note, multiline: true) { Height = 64, Margin = new Padding(0, 0, 0, 10) });

        _glossary = new ToggleSwitch { AccessibleName = "Mein Glossar mitsenden" };
        _prompts = new ToggleSwitch { AccessibleName = "Meine Anweisungen mitsenden" };
        foreach (var (toggle, label) in new[] { (_glossary, "Mein Glossar mitsenden"), (_prompts, "Meine Anweisungen mitsenden") })
        {
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            toggle.Margin = new Padding(0, 0, 10, 0);
            line.Controls.Add(toggle);
            line.Controls.Add(new ThemedLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) });
            Add(line);
            toggle.CheckedChanged += (_, _) => Rebuild();
        }
        _lockHint = Hint("");
        Add(_lockHint);

        _draft = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Font = new Font("Consolas", 9.5f), AccessibleName = "Text, der gesendet wird",
        };
        _draft.TextChanged += (_, _) =>
        {
            if (!_rebuilding && !_edited) { _edited = true; UpdateLock(); }
            Changed();
        };
        _note.TextChanged += (_, _) => Changed();
        Add(new FieldFrame(_draft, multiline: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) }, fill: true);

        _tooLong = Hint("Zu lang zum Vorausfüllen. Beim Senden wird der Text stattdessen kopiert: Füge ihn mit Strg+V in den Chat "
            + "oder die E-Mail ein, die sich öffnet.");
        Add(_tooLong);

        var cancel = new FluentButton("Abbrechen") { MinimumSize = new Size(100, 32), AccessibleName = "Abbrechen" };
        cancel.Click += (_, _) => Close();
        _startOver = new FluentButton("Neu beginnen") { MinimumSize = new Size(110, 32), Margin = new Padding(8, 0, 0, 0), Visible = false, AccessibleName = "Neu beginnen" };
        _startOver.Click += (_, _) => { _edited = false; UpdateLock(); Rebuild(); };
        _copy = new FluentButton("Kopieren") { MinimumSize = new Size(100, 32), AccessibleName = "Kopieren" };
        _copy.Click += (_, _) => { if (CopyMessage()) _copy.Text = "Kopiert!"; };
        var mail = new FluentButton("Per E-Mail senden") { MinimumSize = new Size(140, 32), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Per E-Mail senden" };
        mail.Click += (_, _) => Send(Core.FeedbackChannel.Email);
        var whatsApp = new FluentButton("Per WhatsApp senden", accent: true) { MinimumSize = new Size(160, 32), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Per WhatsApp senden" };
        whatsApp.Click += (_, _) => Send(Core.FeedbackChannel.WhatsApp);

        var buttons = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        left.Controls.Add(cancel);
        left.Controls.Add(_startOver);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right };
        right.Controls.Add(_copy);
        right.Controls.Add(mail);
        right.Controls.Add(whatsApp);
        buttons.Controls.Add(left, 0, 0);
        buttons.Controls.Add(right, 1, 0);
        Add(buttons);

        Controls.Add(root);
        CancelButton = cancel;
        ResumeLayout(true);

        UpdateLock();
        Rebuild();
        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => _note.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    private string Message => Core.ConfigurationReport.Message(_note.Text, _draft.Text.Replace("\r\n", "\n"));

    private void Rebuild()
    {
        if (_edited) return;
        _rebuilding = true;
        _draft.Text = _build(new Core.ConfigurationReportOptions(_glossary.Checked, _prompts.Checked)).Replace("\n", "\r\n");
        _rebuilding = false;
        Changed();
    }

    private void UpdateLock()
    {
        _glossary.Enabled = _prompts.Enabled = !_edited;
        _startOver.Visible = _edited;
        _lockHint.Text = _edited
            ? "Du hast den Text unten bearbeitet, daher sind diese Optionen gesperrt. Mit „Neu beginnen“ kannst du sie wieder ändern."
            : "Glossar und Anweisungen können Namen enthalten. Beides bleibt draußen, solange du es nicht anschaltest.";
    }

    private void Changed()
    {
        _copy.Text = "Kopieren";
        _tooLong.Visible = !Core.ConfigurationReport.FitsInLink(Message);
    }

    private bool CopyMessage()
    {
        try
        {
            Clipboard.SetText(Message);
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Error($"Copying the settings report failed: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>A short message goes prefilled; a long one would be dropped by the link, so it goes through the clipboard (Mac).</summary>
    private void Send(Core.FeedbackChannel channel)
    {
        var message = Message;
        bool fits = Core.ConfigurationReport.FitsInLink(message);
        if (!fits && !CopyMessage())
        {
            ChoiceDialog.Ask(this, "Kopieren hat nicht geklappt", "Der Text ist zu lang für den Link und ließ sich nicht in die "
                + "Zwischenablage legen. Bitte den Text markieren, mit Strg+C kopieren und es dann noch einmal versuchen.", new[] { "OK" });
            return;
        }
        Core.Log.Info($"Send my settings via {channel} (prefilled: {fits})");
        TrayApp.OpenFeedback(channel, fits ? message : null);
        Close();
    }
}
