using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>Settings window: API key, hotkeys, glossary, template. German UI.</summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color HintColor = SystemColors.GrayText;

    private readonly Core.Settings _settings;
    private readonly TextBox _apiKey;
    private readonly HotkeyBox _dictateKey;
    private readonly HotkeyBox _promptKey;
    private readonly TextBox _glossary;
    private readonly TextBox _template;

    public SettingsForm(Core.Settings settings)
    {
        _settings = settings;

        Text = "WhisperShortcut – Einstellungen";
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 760);
        MinimumSize = new Size(560, 560);
        MaximizeBox = false;
        ShowInTaskbar = true;
        Icon = TrayIcons.Ready;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 12),
            ColumnCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- API key ---
        root.Controls.Add(Header("Gemini-API-Schlüssel"));
        _apiKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Text = settings.ApiKey ?? "" };
        var show = new CheckBox { Text = "Anzeigen", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
        show.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !show.Checked;
        var keyRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyRow.Controls.Add(_apiKey, 0, 0);
        keyRow.Controls.Add(show, 1, 0);
        root.Controls.Add(keyRow);
        root.Controls.Add(Hint("Bitte einen Schlüssel aus einem Google-Projekt mit Abrechnung verwenden. "
            + "Im kostenlosen Kontingent darf Google die Aufnahmen zum Training nutzen."));
        var link = new LinkLabel { Text = "Schlüssel in Google AI Studio erstellen", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        link.LinkClicked += (_, _) => OpenUrl("https://aistudio.google.com/apikey");
        root.Controls.Add(link);

        // --- hotkeys ---
        root.Controls.Add(Header("Tastenkürzel"));
        _dictateKey = new HotkeyBox(settings.DictateHotkey);
        _promptKey = new HotkeyBox(settings.PromptHotkey);
        var keys = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        keys.Controls.Add(RowLabel("Diktieren"), 0, 0);
        keys.Controls.Add(_dictateKey, 1, 0);
        keys.Controls.Add(RowLabel("Text bearbeiten oder schreiben lassen"), 0, 1);
        keys.Controls.Add(_promptKey, 1, 1);
        root.Controls.Add(keys);
        root.Controls.Add(Hint("Ins Feld klicken und die neue Kombination drücken. Kombinationen mit Alt besser meiden: "
            + "Danach öffnen viele Programme ihr Menü, und Markieren und Einfügen klappen nicht mehr."));

        // --- glossary ---
        root.Controls.Add(Header("Glossar"));
        root.Controls.Add(Hint("Ein Begriff pro Zeile: Namen, Produkte und Fachwörter, die richtig geschrieben werden sollen."));
        _glossary = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Height = 90, Text = string.Join("\r\n", settings.Glossary ?? new List<string>()),
        };
        root.Controls.Add(_glossary);

        // --- template ---
        var templateHeader = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        templateHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        templateHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var templateTitle = Header("Vorlage");
        templateTitle.Margin = new Padding(0);
        templateHeader.Controls.Add(templateTitle, 0, 0);
        var reset = new LinkLabel { Text = "Beispielvorlage wiederherstellen", AutoSize = true, Anchor = AnchorStyles.Right };
        templateHeader.Controls.Add(reset, 1, 0);
        root.Controls.Add(templateHeader);
        root.Controls.Add(Hint("Gilt, wenn beim Bearbeiten-Kürzel nichts markiert ist: So soll ein diktiertes Angebot "
            + "oder eine Mail aussehen. Leer lassen = ohne Vorlage."));
        _template = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = true,
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
        var save = new Button { Text = "Speichern", AutoSize = true, MinimumSize = new Size(100, 30) };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        root.Controls.Add(buttons);
        CancelButton = cancel;

        for (int i = 0; i < root.Controls.Count; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // The template box takes all remaining height.
        root.RowCount = root.Controls.Count;
        root.RowStyles[root.Controls.IndexOf(_template)] = new RowStyle(SizeType.Percent, 100);
        Controls.Add(root);

        // Small laptop screens (and the 768 px VM): never taller than the work area, the template box shrinks.
        Load += (_, _) =>
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (Height > area.Height) { Height = area.Height; Top = area.Top; }
        };
        Shown += (_, _) => (string.IsNullOrEmpty(_apiKey.Text) ? (Control)_apiKey : save).Focus();
    }

    private void Save()
    {
        if (!Hotkey.TryParse(_dictateKey.Value, out var dictate) || !Hotkey.TryParse(_promptKey.Value, out var prompt))
        {
            Warn("Bitte für beide Funktionen ein Tastenkürzel mit Strg oder Alt festlegen.");
            return;
        }
        if (dictate == prompt)
        {
            Warn("Die beiden Tastenkürzel müssen verschieden sein.");
            return;
        }

        _settings.ApiKey = _apiKey.Text.Trim();
        _settings.DictateHotkey = dictate.ToString();
        _settings.PromptHotkey = prompt.ToString();
        _settings.Glossary = _glossary.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
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
        Core.Log.Info($"Settings saved (key set: {_settings.ApiKey.Length > 0}, glossary {_settings.Glossary.Count}, template {_settings.PromptSystemPrompt.Length} chars)");
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 14, 0, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = HintColor, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 4),
        MaximumSize = new Size(600, 0),
    };

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
            if (e.KeyCode == Keys.Tab && e.Modifiers == Keys.None)
            {
                Parent?.SelectNextControl(this, true, true, true, true);
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
            if (!modifiers.HasFlag(Keys.Control) && !modifiers.HasFlag(Keys.Alt))
            {
                Text = "Strg oder Alt dazunehmen";
                return;
            }
            var stored = (modifiers.HasFlag(Keys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(Keys.Alt) ? "Alt+" : "")
                + (modifiers.HasFlag(Keys.Shift) ? "Shift+" : "") + key;
            if (Hotkey.TryParse(stored, out var parsed))
            {
                Value = parsed.ToString();
                Text = parsed.ToDisplayString();
            }
        }
    }
}
