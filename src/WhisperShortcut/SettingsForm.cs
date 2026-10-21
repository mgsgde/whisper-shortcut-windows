using System.Diagnostics;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>Settings window: online/offline, API key, offline models, hotkeys, glossary, template. German UI.</summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color HintColor = SystemColors.GrayText;

    private readonly Core.Settings _settings;
    private readonly OfflineEngine _offline;
    private readonly RadioButton _onlineMode;
    private readonly RadioButton _offlineMode;
    private readonly Control[] _onlineOnly;
    private readonly Control[] _offlineOnly;
    private readonly ModelRow _speechRow;
    private readonly ModelRow _llmRow;
    private readonly ComboBox _llmChoice;
    private readonly TextBox _apiKey;
    private readonly HotkeyBox _dictateKey;
    private readonly HotkeyBox _promptKey;
    private readonly HotkeyBox _glossaryKey;
    private readonly CheckBox _autoStart;
    private readonly TextBox _glossary;
    private readonly TextBox _template;
    private readonly TextBox _quickActions;
    private readonly List<Label> _hints = new();

    public SettingsForm(Core.Settings settings, OfflineEngine offline)
    {
        _settings = settings;
        _offline = offline;

        // Designer pattern: all sizes below are written for 96 dpi and scaled once on ResumeLayout,
        // so 125/150/200 % get the same layout instead of big text in a small window.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Einstellungen";
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.Manual; // centered in Load, after the size is known
        ClientSize = new Size(640, 720);
        MinimumSize = new Size(560, 400);
        MaximizeBox = false;
        ShowInTaskbar = true;
        Icon = TrayIcons.App;

        // Content scrolls; the buttons stay visible below it.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 16, 20, 12),
            ColumnCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- online / offline ---
        root.Controls.Add(Header("Arbeitsweise"));
        _onlineMode = new RadioButton { Text = "Online mit Gemini", AutoSize = true, Checked = !settings.OfflineMode, Margin = new Padding(0, 2, 0, 0) };
        var onlineHint = Hint("Beste Qualität und am schnellsten. Braucht Internet und einen API-Schlüssel; "
            + "Aufnahme und markierter Text gehen an Google.");
        onlineHint.Margin = new Padding(18, 0, 0, 6);
        _offlineMode = new RadioButton { Text = "Offline auf diesem PC", AutoSize = true, Checked = settings.OfflineMode, Margin = new Padding(0, 2, 0, 0) };
        var offlineHint = Hint("Aufnahmen und Texte verlassen den Rechner nie. Die Modelle werden einmal geladen, "
            + "danach geht alles ohne Internet. „Text bearbeiten“ dauert offline länger, je nach PC einige Sekunden bis eine Minute.");
        offlineHint.Margin = new Padding(18, 0, 0, 4);
        root.Controls.Add(_onlineMode);
        root.Controls.Add(onlineHint);
        root.Controls.Add(_offlineMode);
        root.Controls.Add(offlineHint);

        // --- offline models ---
        var offlineHeader = Header("Offline-Modelle");
        root.Controls.Add(offlineHeader);
        var speech = Core.OfflineCatalog.Parakeet;
        _speechRow = new ModelRow("Spracherkennung", "Für Diktieren und für gesprochene Anweisungen.");
        root.Controls.Add(_speechRow);

        _llmChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };
        var recommended = Core.OfflineCatalog.Recommended(OfflineEngine.TotalRam);
        foreach (var llm in Core.OfflineCatalog.Llms) _llmChoice.Items.Add(new LlmItem(llm, llm.Id == recommended.Id));
        _llmChoice.SelectedItem = _llmChoice.Items.Cast<LlmItem>().First(i => i.Llm.Id == offline.SelectedLlm.Id);
        _llmRow = new ModelRow("Sprachmodell für „Text bearbeiten oder schreiben lassen“", null);
        root.Controls.Add(_llmRow);
        root.Controls.Add(_llmChoice);
        var ramHint = Hint($"Dieser PC hat {FormatBytes(OfflineEngine.TotalRam, binary: true)} Arbeitsspeicher. "
            + "Das größere Modell schreibt bessere Texte, braucht aber mehr Speicher und Zeit.");
        root.Controls.Add(ramHint);

        _speechRow.Action.Click += (_, _) => ToggleDownload("speech", new[] { speech });
        _speechRow.Remove.LinkClicked += (_, _) => RemoveModel(speech, "die Offline-Spracherkennung");
        _llmRow.Action.Click += (_, _) => ToggleDownload("llm",
            new[] { Core.OfflineCatalog.LlamaServer(OfflineEngine.Arch), SelectedLlmChoice.Component });
        _llmRow.Remove.LinkClicked += (_, _) => RemoveModel(SelectedLlmChoice.Component, SelectedLlmChoice.DisplayName);
        _llmChoice.SelectedIndexChanged += (_, _) => UpdateModelRows();
        _offline.DownloadChanged += UpdateModelRows;
        FormClosed += (_, _) => _offline.DownloadChanged -= UpdateModelRows;
        _offlineOnly = new Control[] { offlineHeader, _speechRow, _llmRow, _llmChoice, ramHint };

        // --- API key ---
        var keyHeader = Header("Gemini-API-Schlüssel");
        root.Controls.Add(keyHeader);
        _apiKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Text = settings.ApiKey ?? "" };
        var show = new CheckBox { Text = "Anzeigen", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
        show.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !show.Checked;
        var keyRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyRow.Controls.Add(_apiKey, 0, 0);
        keyRow.Controls.Add(show, 1, 0);
        root.Controls.Add(keyRow);
        var keyHint = Hint("Ohne Schlüssel funktioniert der Online-Modus nicht. Bitte einen Schlüssel aus einem Google-Projekt "
            + "mit Abrechnung verwenden: Im kostenlosen Kontingent darf Google die Aufnahmen zum Training nutzen.");
        root.Controls.Add(keyHint);
        var link = new LinkLabel { Text = "Schlüssel in Google AI Studio erstellen", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        link.LinkClicked += (_, _) => OpenUrl("https://aistudio.google.com/apikey");
        root.Controls.Add(link);
        _onlineOnly = new Control[] { keyHeader, keyRow, keyHint, link };
        _onlineMode.CheckedChanged += (_, _) => UpdateMode();

        // --- hotkeys ---
        root.Controls.Add(Header("Tastenkürzel"));
        _dictateKey = new HotkeyBox(settings.DictateHotkey);
        _promptKey = new HotkeyBox(settings.PromptHotkey);
        _glossaryKey = new HotkeyBox(settings.GlossaryHotkey);
        var keys = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        keys.Controls.Add(RowLabel("Diktieren"), 0, 0);
        keys.Controls.Add(_dictateKey, 1, 0);
        keys.Controls.Add(RowLabel("Text bearbeiten oder schreiben lassen"), 0, 1);
        keys.Controls.Add(_promptKey, 1, 1);
        keys.Controls.Add(RowLabel("Markiertes Wort ins Glossar"), 0, 2);
        keys.Controls.Add(_glossaryKey, 1, 2);
        root.Controls.Add(keys);
        root.Controls.Add(Hint("Ins Feld klicken und die neue Kombination drücken, z. B. Strg+Umschalt+D. "
            + "Kombinationen mit Alt besser meiden, sonst öffnen viele Programme ihr Menü. "
            + "Esc bricht eine laufende Aufnahme ab. Solange dieses Fenster im Vordergrund ist, sind die Kürzel aus."));

        // --- start ---
        root.Controls.Add(Header("Start"));
        _autoStart = new CheckBox { Text = "Mit Windows starten", AutoSize = true, Checked = AutoStart.IsEnabled, Margin = new Padding(0, 2, 0, 0) };
        root.Controls.Add(_autoStart);
        var autoStartHint = Hint("Dann ist WhisperShortcut nach jedem Neustart gleich unten rechts im Infobereich da.");
        autoStartHint.Margin = new Padding(18, 0, 0, 4);
        root.Controls.Add(autoStartHint);

        // --- glossary ---
        root.Controls.Add(Header("Glossar"));
        root.Controls.Add(Hint("Ein Begriff pro Zeile: Namen, Produkte und Fachwörter, die richtig geschrieben werden sollen. "
            + $"Schneller: das richtig geschriebene Wort irgendwo markieren und {Hotkey.Display(settings.GlossaryHotkey)} drücken. "
            + "Gilt auch offline; dort braucht die Spracherkennung nach einer Änderung einige Sekunden."));
        _glossary = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Height = 96, Text = string.Join("\r\n", settings.Glossary ?? new List<string>()),
        };
        root.Controls.Add(_glossary);

        // --- quick instructions ---
        root.Controls.Add(Header("Schnellanweisungen"));
        root.Controls.Add(Hint($"Erscheinen beim Bearbeiten ({Hotkey.Display(settings.PromptHotkey)}) unter der Anzeige. "
            + "Strg+1 bis Strg+5 oder ein Klick wendet sie sofort an: auf die Markierung oder, ohne Markierung, "
            + "auf das gerade Diktierte. Eine pro Zeile, höchstens fünf."));
        _quickActions = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Height = 100, Text = string.Join("\r\n", settings.QuickActions),
        };
        root.Controls.Add(_quickActions);

        // --- template ---
        var templateHeader = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        templateHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        templateHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var templateTitle = Header("Vorlage für neue Texte");
        templateTitle.Margin = new Padding(0);
        templateHeader.Controls.Add(templateTitle, 0, 0);
        var reset = new LinkLabel { Text = "Beispielvorlage wiederherstellen", AutoSize = true, Anchor = AnchorStyles.Right };
        templateHeader.Controls.Add(reset, 1, 0);
        root.Controls.Add(templateHeader);
        root.Controls.Add(Hint("So soll ein Text aussehen, der ohne Markierung geschrieben wird (z. B. ein Angebot "
            + "oder eine Mail). Leer lassen = ohne Vorlage."));
        _template = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = true, Height = 220,
            Text = (settings.PromptSystemPrompt ?? "").ReplaceLineEndings("\r\n"),
        };
        reset.LinkClicked += (_, _) =>
        {
            if (_template.Text.Trim().Length == 0
                || MessageBox.Show(this, "Die eigene Vorlage wird durch die Beispielvorlage ersetzt.", "Vorlage ersetzen?",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                _template.Text = QuoteTemplate.Default.ReplaceLineEndings("\r\n");
            }
        };
        root.Controls.Add(_template);

        // --- buttons ---
        var save = new Button { Text = "Speichern", AutoSize = true, MinimumSize = new Size(100, 30), TabIndex = 0 };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, MinimumSize = new Size(100, 30), TabIndex = 1 };
        save.Click += (_, _) => Save();
        // The window is modeless (Show, not ShowDialog), so a DialogResult alone would not close it.
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true,
            Padding = new Padding(20, 10, 20, 12),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        CancelButton = cancel;
        AcceptButton = save; // Enter in the key field saves; the multi-line boxes keep their Enter

        for (int i = 0; i < root.Controls.Count; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowCount = root.Controls.Count;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, TabIndex = 0 };
        scroll.Controls.Add(root);
        buttons.TabIndex = 1;
        // Docking runs in reverse order: the buttons claim the bottom first, the scroll area takes the rest.
        Controls.Add(scroll);
        Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlDark });
        Controls.Add(buttons);
        ResumeLayout(false);
        PerformLayout();

        // Hints wrap at the visible width (scaled, with or without scroll bar), never wider.
        scroll.ClientSizeChanged += (_, _) => FitHints(scroll.ClientSize.Width - root.Padding.Horizontal);

        // Fit the window to its content, but never larger than the work area (1366x768 laptops,
        // 150 % on small screens): then the content scrolls and the buttons stay visible.
        Load += (_, _) =>
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Math.Min(Width, area.Width);
            FitHints(scroll.ClientSize.Width - root.Padding.Horizontal);
            int chrome = Height - ClientSize.Height;
            int content = root.GetPreferredSize(new Size(scroll.ClientSize.Width, 0)).Height + 1 + buttons.Height;
            Height = Math.Min(content + chrome, area.Height);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        };
        UpdateMode();
        UpdateModelRows();
        Shown += (_, _) => (string.IsNullOrEmpty(_apiKey.Text) && _onlineMode.Checked ? (Control)_apiKey : save).Focus();
    }

    // ---------------- offline models ----------------

    private Core.LocalLlm SelectedLlmChoice => ((LlmItem)_llmChoice.SelectedItem!).Llm;

    /// <summary>Online shows the key, offline shows the models. Hidden, not greyed out: one way of working at a time.</summary>
    private void UpdateMode()
    {
        SuspendLayout();
        foreach (var c in _onlineOnly) c.Visible = _onlineMode.Checked;
        foreach (var c in _offlineOnly) c.Visible = _offlineMode.Checked;
        ResumeLayout(true);
    }

    private void ToggleDownload(string id, IReadOnlyList<Core.OfflineComponent> components)
    {
        var running = _offline.CurrentDownload;
        if (running != null)
        {
            if (running.Id == id) running.Cancel.Cancel();
            return;
        }
        _offline.StartDownload(id, components);
    }

    private void RemoveModel(Core.OfflineComponent component, string name)
    {
        if (MessageBox.Show(this, $"{name} wird von diesem PC gelöscht. Für den Offline-Modus muss es danach neu geladen werden.",
                "Modell entfernen?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { _offline.Delete(component); }
        catch (Exception e)
        {
            Core.Log.Error("Deleting model failed", e);
            Warn("Das Modell konnte nicht gelöscht werden: " + e.Message);
        }
        UpdateModelRows();
    }

    private void UpdateModelRows()
    {
        if (IsDisposed) return;
        var download = _offline.CurrentDownload;
        var llm = SelectedLlmChoice;
        var llmParts = new[] { Core.OfflineCatalog.LlamaServer(OfflineEngine.Arch), llm.Component };
        UpdateRow(_speechRow, "speech", OfflineEngine.SpeechInstalled, ComponentSize(Core.OfflineCatalog.Parakeet), download);
        UpdateRow(_llmRow, "llm", OfflineEngine.LlmInstalled(llm), llmParts.Sum(ComponentSize), download);
        _llmChoice.Enabled = download?.Id != "llm";
    }

    private void UpdateRow(ModelRow row, string id, bool installed, long size, OfflineEngine.Download? download)
    {
        bool mine = download?.Id == id;
        string? error = download == null && _offline.LastFinished is { Error: { } e } last && last.Id == id ? e : null;
        row.Progress.Visible = mine;
        row.Remove.Visible = installed && !mine;
        row.Action.Visible = !installed || mine;
        row.Action.Enabled = download == null || mine;
        row.Action.Text = mine ? "Abbrechen" : "Laden";
        if (mine)
        {
            int percent = (int)Math.Round(download!.Fraction * 100);
            row.Progress.Value = Math.Clamp(percent, 0, 100);
            row.SetStatus($"Wird geladen … {percent} % von {FormatBytes(size)}", SystemColors.GrayText);
        }
        else if (installed)
        {
            row.SetStatus($"Geladen ({FormatBytes(size)} auf diesem PC)", Color.FromArgb(0, 120, 60));
        }
        else if (error != null)
        {
            row.SetStatus(error, Color.FromArgb(180, 30, 30));
        }
        else
        {
            row.SetStatus($"Noch nicht geladen, ca. {FormatBytes(size)}", SystemColors.GrayText);
        }
    }

    private static long ComponentSize(Core.OfflineComponent c) => c.Files.Sum(f => f.Size);

    /// <summary>"670 MB", "5,7 GB" (German decimal comma). <paramref name="binary"/> for RAM (16 GB, not 17,2).</summary>
    private static string FormatBytes(long bytes, bool binary = false)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        double unit = binary ? 1024.0 * 1024 * 1024 : 1e9;
        if (bytes >= unit) return (bytes / unit).ToString(binary ? "0" : "0.0", de) + " GB";
        return (bytes / (binary ? 1024.0 * 1024 : 1e6)).ToString("0", de) + " MB";
    }

    private sealed record LlmItem(Core.LocalLlm Llm, bool Recommended)
    {
        public override string ToString() => $"{Llm.DisplayName}{(Recommended ? " (empfohlen für diesen PC)" : "")} – {Llm.Description}";
    }

    /// <summary>One model: name, status line, Laden/Abbrechen button, progress bar, "Entfernen" link.</summary>
    private sealed class ModelRow : TableLayoutPanel
    {
        private readonly Label _status;
        public Button Action { get; }
        public LinkLabel Remove { get; }
        public ProgressBar Progress { get; }

        public ModelRow(string title, string? subtitle)
        {
            ColumnCount = 2;
            Dock = DockStyle.Top;
            AutoSize = true;
            Margin = new Padding(0, 4, 0, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            text.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, 2, 0, 0) });
            if (subtitle != null) text.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0) });
            _status = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
            text.Controls.Add(_status);

            Action = new Button { Text = "Laden", AutoSize = true, MinimumSize = new Size(100, 30), Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(12, 2, 0, 0) };
            Remove = new LinkLabel { Text = "Entfernen", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(12, 6, 0, 0) };
            var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            actions.Controls.Add(Action);
            actions.Controls.Add(Remove);

            Progress = new ProgressBar { Dock = DockStyle.Top, Height = 8, Margin = new Padding(0, 6, 0, 0), Visible = false };

            Controls.Add(text, 0, 0);
            Controls.Add(actions, 1, 0);
            Controls.Add(Progress, 0, 1);
            SetColumnSpan(Progress, 2);
        }

        public void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }
    }

    private void FitHints(int width)
    {
        width = Math.Max(width, LogicalToDeviceUnits(200));
        foreach (var hint in _hints) hint.MaximumSize = new Size(width, 0);
    }

    private void Save()
    {
        if (!Hotkey.TryParse(_dictateKey.Value, out var dictate) || !Hotkey.TryParse(_promptKey.Value, out var prompt)
            || !Hotkey.TryParse(_glossaryKey.Value, out var glossary)
            || !IsSafe(dictate) || !IsSafe(prompt) || !IsSafe(glossary))
        {
            Warn("Bitte für jede Funktion ein Tastenkürzel mit zwei Zusatztasten festlegen, z. B. Strg+Umschalt+D.");
            return;
        }
        if (dictate == prompt || dictate == glossary || prompt == glossary)
        {
            Warn("Die Tastenkürzel müssen verschieden sein.");
            return;
        }

        var quickLines = _quickActions.Lines.Where(l => l.Trim().Length > 0).ToList();
        if (quickLines.Any(l => l.Trim().Length > Core.QuickActionList.MaxLength))
        {
            Warn($"Eine Schnellanweisung ist länger als {Core.QuickActionList.MaxLength} Zeichen. Bitte kürzer fassen.");
            return;
        }
        if (quickLines.Count > Core.QuickActionList.Max)
        {
            Warn($"Höchstens {Core.QuickActionList.Max} Schnellanweisungen (Strg+1 bis Strg+{Core.QuickActionList.Max}). Bitte eine Zeile entfernen.");
            return;
        }

        _settings.OfflineMode = _offlineMode.Checked;
        _settings.OfflineLlmId = SelectedLlmChoice.Id;
        _settings.ApiKey = _apiKey.Text.Trim();
        _settings.DictateHotkey = dictate.ToString();
        _settings.PromptHotkey = prompt.ToString();
        _settings.GlossaryHotkey = glossary.ToString();
        _settings.Glossary = _glossary.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        _settings.QuickActions = Core.QuickActionList.Clean(quickLines);
        _settings.PromptSystemPrompt = _template.Text.Trim().Length == 0 ? "" : _template.Text.ReplaceLineEndings("\n").Trim();
        try
        {
            _settings.Save();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Saving settings failed: {e.GetType().Name}");
            Warn("Die Einstellungen konnten nicht gespeichert werden: " + e.Message);
            return;
        }
        if (_autoStart.Checked != AutoStart.IsEnabled)
        {
            if (!(_autoStart.Checked ? AutoStart.Enable() : AutoStart.Disable()))
                Warn("Der automatische Start konnte nicht geändert werden. Details stehen im Protokoll.");
        }
        Core.Log.Info($"Settings saved (autostart {AutoStart.IsEnabled}, offline {_settings.OfflineMode}, llm {_settings.OfflineLlmId}, key set: {_settings.ApiKey.Length > 0}, glossary {_settings.Glossary.Count}, quick actions {_settings.QuickActions.Count}, template {_settings.PromptSystemPrompt.Length} chars)");
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>The glossary hotkey added a term while this window is open: show it, so Save keeps it.</summary>
    public void AppendGlossaryTerm(string term)
    {
        var lines = _glossary.Lines.ToList();
        if (lines.Any(l => string.Equals(l.Trim(), term, StringComparison.CurrentCultureIgnoreCase))) return;
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(term);
        _glossary.Text = string.Join("\r\n", lines);
    }

    /// <summary>
    /// Two of Ctrl/Alt/Shift, one of them Ctrl or Alt. Ctrl+V or Alt+F as a global hotkey would take
    /// that key away from every program.
    /// </summary>
    private static bool IsSafe(Hotkey hotkey)
    {
        uint m = hotkey.Modifiers;
        int count = ((m & MOD_CONTROL) != 0 ? 1 : 0) + ((m & MOD_ALT) != 0 ? 1 : 0) + ((m & MOD_SHIFT) != 0 ? 1 : 0);
        return count >= 2 && (m & (MOD_CONTROL | MOD_ALT)) != 0;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 14, 0, 4),
    };

    private Label Hint(string text)
    {
        var hint = new Label
        {
            Text = text, AutoSize = true, ForeColor = HintColor, Margin = new Padding(0, 4, 0, 4),
            MaximumSize = new Size(600, 0), // replaced by FitHints once the real width is known
        };
        _hints.Add(hint);
        return hint;
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6),
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening URL failed: {e.GetType().Name}"); }
    }

    /// <summary>Text box that records a key combination instead of text.</summary>
    private sealed class HotkeyBox : TextBox
    {
        public string Value { get; private set; }

        public HotkeyBox(string stored)
        {
            Value = stored;
            Text = Hotkey.Display(stored);
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Dock = DockStyle.Fill;
            Margin = new Padding(0, 3, 0, 3);
            TextAlign = HorizontalAlignment.Center;
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Alt combinations arrive here, not in OnKeyDown.
            if (Focused && (keyData & Keys.Alt) != 0)
            {
                Apply(keyData & Keys.KeyCode, keyData & Keys.Modifiers);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Tab && e.Modifiers is Keys.None or Keys.Shift)
            {
                // Form-wide: the parent is the small hotkey table, where Tab would only bounce between the two boxes.
                FindForm()?.SelectNextControl(this, e.Modifiers == Keys.None, true, true, true);
                return;
            }
            Apply(e.KeyCode, e.Modifiers);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            // Released without a main key: show the stored combination again.
            if (Hotkey.TryParse(Value, out _) && Text.EndsWith('+')) Text = Hotkey.Display(Value);
        }

        protected override void OnLeave(EventArgs e)
        {
            Text = Hotkey.Display(Value);
            base.OnLeave(e);
        }

        private void Apply(Keys key, Keys modifiers)
        {
            var prefix = (modifiers.HasFlag(Keys.Control) ? "Strg+" : "") + (modifiers.HasFlag(Keys.Alt) ? "Alt+" : "")
                + (modifiers.HasFlag(Keys.Shift) ? "Umschalt+" : "");
            if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None)
            {
                Text = prefix;
                return;
            }
            var stored = (modifiers.HasFlag(Keys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(Keys.Alt) ? "Alt+" : "")
                + (modifiers.HasFlag(Keys.Shift) ? "Shift+" : "") + key;
            if (!Hotkey.TryParse(stored, out var parsed) || !IsSafe(parsed))
            {
                Text = "Bitte Strg+Umschalt+… verwenden";
                return;
            }
            Value = parsed.ToString();
            Text = parsed.ToDisplayString();
        }
    }
}

