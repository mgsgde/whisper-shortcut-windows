using System.Diagnostics;
using System.Drawing.Drawing2D;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// Settings window in the style of the Windows 11 settings: pages on the left (Allgemein, Tastenkürzel,
/// Glossar, Schnellanweisungen, Vorlage), the page on the right, Speichern/Abbrechen below it.
/// The things a practice edits often (glossary, quick instructions, template) each get a page of their
/// own instead of sitting at the end of one long scroll. German UI.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color HintColor = SystemColors.GrayText;
    private static readonly Color NavBack = Color.FromArgb(243, 243, 243);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);
    private static readonly Color Good = Color.FromArgb(15, 123, 15);
    private static readonly Color Bad = Color.FromArgb(196, 43, 28);
    private static readonly Color Caution = Color.FromArgb(157, 93, 0);

    private readonly Core.Settings _settings;
    private readonly OfflineEngine _offline;
    private readonly Core.GeminiClient _gemini;
    private readonly RadioButton _onlineMode;
    private readonly RadioButton _offlineMode;
    private readonly Control[] _onlineOnly;
    private readonly Control[] _offlineOnly;
    private readonly ModelRow _speechRow;
    private readonly ModelRow _llmRow;
    private readonly ComboBox _llmChoice;
    private readonly TextBox _apiKey;
    private readonly Label _keyStatus;
    private readonly ComboBox _mic;
    private readonly Button _micTest;
    private readonly LevelMeter _micMeter;
    private readonly HotkeyBox _dictateKey;
    private readonly HotkeyBox _promptKey;
    private readonly HotkeyBox _glossaryKey;
    private readonly CheckBox _autoStart;
    private readonly TextBox _glossary;
    private readonly Label _glossaryCount;
    private readonly TextBox _template;
    private readonly TextBox[] _quick = new TextBox[Core.QuickActionList.Max];
    private readonly Label _dirty;
    private readonly List<Label> _hints = new();
    private readonly List<NavItem> _nav = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Panel _pageHost;
    private readonly System.Windows.Forms.Timer _keyDebounce = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    private CancellationTokenSource? _keyCheck;
    private LevelProbe? _probe;
    private string _snapshot = "";
    private bool _saved;

    public const string General = "Allgemein", Keys_ = "Tastenkürzel", GlossaryPage = "Glossar",
        QuickPage = "Schnellanweisungen", TemplatePage = "Vorlage";

    /// <param name="welcome">First start: a short "what to do now" box on top of the first page.</param>
    public SettingsForm(Core.Settings settings, OfflineEngine offline, Core.GeminiClient gemini, bool welcome = false)
    {
        _settings = settings;
        _offline = offline;
        _gemini = gemini;

        // Designer pattern: all sizes below are written for 96 dpi and scaled once on ResumeLayout,
        // so 125/150/200 % get the same layout instead of big text in a small window.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Einstellungen";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.Manual; // centered in Load, after the size is known
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(720, 500);
        MaximizeBox = false;
        ShowInTaskbar = true;
        Icon = TrayIcons.App;

        // ================= page: Allgemein =================
        var general = NewPage(General);
        if (welcome)
        {
            general.Controls.Add(Banner("Willkommen! Zuerst die Arbeitsweise wählen: Online braucht einen Gemini-Schlüssel, "
                + "offline werden einmal die Modelle geladen. Dann „Speichern“. Danach in einem beliebigen Programm "
                + $"{Hotkey.Display(settings.DictateHotkey)} drücken, sprechen und noch einmal drücken."));
        }
        general.Controls.Add(Header("Arbeitsweise"));
        _onlineMode = new RadioButton { Text = "Online mit Gemini", AutoSize = true, Checked = !settings.OfflineMode, Margin = new Padding(0, 2, 0, 0) };
        var onlineHint = Hint("Beste Qualität und am schnellsten. Braucht Internet und einen API-Schlüssel; "
            + "Aufnahme und markierter Text gehen an Google.");
        onlineHint.Margin = new Padding(18, 0, 0, 6);
        _offlineMode = new RadioButton { Text = "Offline auf diesem PC", AutoSize = true, Checked = settings.OfflineMode, Margin = new Padding(0, 2, 0, 0) };
        var offlineHint = Hint("Aufnahmen und Texte verlassen den Rechner nie, richtig für Patientendaten. Die Modelle werden einmal "
            + "geladen, danach geht alles ohne Internet. „Text bearbeiten“ dauert offline länger, je nach PC einige Sekunden bis eine Minute.");
        offlineHint.Margin = new Padding(18, 0, 0, 4);
        general.Controls.Add(_onlineMode);
        general.Controls.Add(onlineHint);
        general.Controls.Add(_offlineMode);
        general.Controls.Add(offlineHint);

        // --- API key (online) ---
        var keyHeader = Header("Gemini-API-Schlüssel");
        general.Controls.Add(keyHeader);
        _apiKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Text = settings.ApiKey ?? "", Margin = new Padding(0, 3, 0, 3) };
        var show = new CheckBox { Text = "Anzeigen", AutoSize = true, Margin = new Padding(10, 6, 0, 0) };
        show.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !show.Checked;
        var check = new Button { Text = "Prüfen", AutoSize = true, MinimumSize = new Size(84, 28), Margin = new Padding(8, 1, 0, 0) };
        var keyRow = Row(_apiKey, show, check);
        general.Controls.Add(keyRow);
        _keyStatus = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 0), Text = "" };
        general.Controls.Add(_keyStatus);
        var keyHint = Hint("Bitte einen Schlüssel aus einem Google-Projekt mit Abrechnung verwenden: Im kostenlosen Kontingent "
            + "darf Google die Aufnahmen zum Training nutzen.");
        general.Controls.Add(keyHint);
        var link = new LinkLabel { Text = "Schlüssel in Google AI Studio erstellen", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        link.LinkClicked += (_, _) => OpenUrl("https://aistudio.google.com/apikey");
        general.Controls.Add(link);
        check.Click += (_, _) => _ = CheckKeyAsync();
        _apiKey.TextChanged += (_, _) =>
        {
            // Pasting a key is the moment to know whether it works, not after Speichern.
            _keyStatus.Text = "";
            _keyDebounce.Stop();
            if (_apiKey.Text.Trim().Length >= 20) _keyDebounce.Start();
        };
        _keyDebounce.Tick += (_, _) => { _keyDebounce.Stop(); _ = CheckKeyAsync(); };
        _onlineOnly = new Control[] { keyHeader, keyRow, _keyStatus, keyHint, link };

        // --- offline models ---
        var offlineHeader = Header("Offline-Modelle");
        general.Controls.Add(offlineHeader);
        var speech = Core.OfflineCatalog.Parakeet;
        _speechRow = new ModelRow("Spracherkennung", "Für Diktieren und für gesprochene Anweisungen.");
        general.Controls.Add(_speechRow);
        _llmChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };
        var recommended = Core.OfflineCatalog.Recommended(OfflineEngine.TotalRam);
        foreach (var llm in Core.OfflineCatalog.Llms) _llmChoice.Items.Add(new LlmItem(llm, llm.Id == recommended.Id));
        _llmChoice.SelectedItem = _llmChoice.Items.Cast<LlmItem>().First(i => i.Llm.Id == offline.SelectedLlm.Id);
        _llmRow = new ModelRow("Sprachmodell für „Text bearbeiten oder schreiben lassen“", null);
        general.Controls.Add(_llmRow);
        general.Controls.Add(_llmChoice);
        var ramHint = Hint($"Dieser PC hat {FormatBytes(OfflineEngine.TotalRam, binary: true)} Arbeitsspeicher. "
            + "Das größere Modell schreibt bessere Texte, braucht aber mehr Speicher und Zeit.");
        general.Controls.Add(ramHint);
        _speechRow.Action.Click += (_, _) => ToggleDownload("speech", new[] { speech });
        _speechRow.Remove.LinkClicked += (_, _) => RemoveModel(speech, "die Offline-Spracherkennung");
        _llmRow.Action.Click += (_, _) => ToggleDownload("llm",
            new[] { Core.OfflineCatalog.LlamaServer(OfflineEngine.Arch), SelectedLlmChoice.Component });
        _llmRow.Remove.LinkClicked += (_, _) => RemoveModel(SelectedLlmChoice.Component, SelectedLlmChoice.DisplayName);
        _llmChoice.SelectedIndexChanged += (_, _) => UpdateModelRows();
        _offline.DownloadChanged += UpdateModelRows;
        FormClosed += (_, _) => _offline.DownloadChanged -= UpdateModelRows;
        _offlineOnly = new Control[] { offlineHeader, _speechRow, _llmRow, _llmChoice, ramHint };
        _onlineMode.CheckedChanged += (_, _) => UpdateMode();

        // --- microphone ---
        general.Controls.Add(Header("Mikrofon"));
        _mic = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        FillMicrophones(settings.Microphone);
        _micTest = new Button { Text = "Testen", AutoSize = true, MinimumSize = new Size(84, 28), Margin = new Padding(8, 1, 0, 0) };
        general.Controls.Add(Row(_mic, _micTest));
        _micMeter = new LevelMeter { Dock = DockStyle.Top, Height = 8, Margin = new Padding(0, 6, 0, 2), Visible = false };
        general.Controls.Add(_micMeter);
        general.Controls.Add(Hint("„Windows-Standard“ folgt dem Gerät, das in Windows unter System → Sound als Eingabe gewählt ist. "
            + "Ein Headset ist in der Praxis deutlich besser als das eingebaute Mikrofon."));
        _micTest.Click += (_, _) => ToggleMicTest();
        _mic.SelectedIndexChanged += (_, _) => { if (_probe != null) { StopMicTest(); ToggleMicTest(); } };
        _meterTimer.Tick += (_, _) => _micMeter.Level = _probe?.Level ?? 0;

        // --- start ---
        general.Controls.Add(Header("Start"));
        _autoStart = new CheckBox { Text = "Mit Windows starten", AutoSize = true, Checked = AutoStart.IsEnabled, Margin = new Padding(0, 2, 0, 0) };
        general.Controls.Add(_autoStart);
        var autoStartHint = Hint("Dann ist WhisperShortcut nach jedem Neustart gleich unten rechts im Infobereich da.");
        autoStartHint.Margin = new Padding(18, 0, 0, 4);
        general.Controls.Add(autoStartHint);

        // ================= page: Tastenkürzel =================
        var keys = NewPage(Keys_);
        keys.Controls.Add(Hint("Ins Feld klicken und die neue Kombination drücken, z. B. Strg+Umschalt+D. Kombinationen mit Alt "
            + "besser meiden, sonst öffnen viele Programme ihr Menü. Solange dieses Fenster im Vordergrund ist, sind die Kürzel aus."));
        _dictateKey = new HotkeyBox(settings.DictateHotkey);
        _promptKey = new HotkeyBox(settings.PromptHotkey);
        _glossaryKey = new HotkeyBox(settings.GlossaryHotkey);
        var keyTable = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        keyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        AddKeyRow(keyTable, 0, "Diktieren", "Text an die Cursorstelle schreiben", _dictateKey);
        AddKeyRow(keyTable, 1, "Text bearbeiten oder schreiben lassen", "Markierung umformulieren oder neuen Text nach Vorlage", _promptKey);
        AddKeyRow(keyTable, 2, "Markiertes Wort ins Glossar", "Damit Namen und Fachwörter künftig richtig geschrieben werden", _glossaryKey);
        keys.Controls.Add(keyTable);
        keys.Controls.Add(Header("Während einer Aufnahme"));
        var fixedKeys = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        fixedKeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        fixedKeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFixedKey(fixedKeys, 0, "Esc", "Aufnahme verwerfen oder Verarbeitung abbrechen");
        AddFixedKey(fixedKeys, 1, "Strg+1 bis Strg+5", "Schnellanweisung anwenden (nur beim Bearbeiten)");
        keys.Controls.Add(fixedKeys);
        keys.Controls.Add(Hint("Diese Tasten sind nur belegt, solange die Anzeige unten zu sehen ist. Sonst gehören sie wie gewohnt "
            + "den anderen Programmen."));

        // ================= page: Glossar =================
        var glossaryHint = Hint("Ein Begriff pro Zeile: Namen, Medikamente, Fachwörter, die richtig geschrieben werden sollen. "
            + $"Schneller: das richtig geschriebene Wort irgendwo markieren und {Hotkey.Display(settings.GlossaryHotkey)} drücken. "
            + "Gilt auch offline; dort braucht die Spracherkennung nach einer Änderung einige Sekunden.");
        _glossary = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = string.Join("\r\n", settings.Glossary ?? new List<string>()), Margin = new Padding(0, 8, 0, 4),
        };
        _glossaryCount = new Label { AutoSize = true, ForeColor = HintColor, Margin = new Padding(0, 2, 0, 0) };
        _glossary.TextChanged += (_, _) => UpdateGlossaryCount();
        UpdateGlossaryCount();
        NewFillPage(GlossaryPage, glossaryHint, null, _glossary, _glossaryCount);

        // ================= page: Schnellanweisungen =================
        var quick = NewPage(QuickPage);
        quick.Controls.Add(Hint($"Erscheinen beim Bearbeiten ({Hotkey.Display(settings.PromptHotkey)}) unten in der Anzeige. "
            + "Strg+1 bis Strg+5 oder ein Klick wendet sie sofort an: auf die Markierung oder, ohne Markierung, auf das gerade "
            + "Diktierte. Beispiel: Befund diktieren, Strg+1, fertig gegliedert."));
        var quickTable = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        quickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        quickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < _quick.Length; i++)
        {
            quickTable.Controls.Add(new Label { Text = $"Strg+{i + 1}", AutoSize = true, ForeColor = HintColor, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, i);
            _quick[i] = new TextBox
            {
                Dock = DockStyle.Fill, MaxLength = Core.QuickActionList.MaxLength, Margin = new Padding(0, 3, 0, 3),
                Text = i < settings.QuickActions.Count ? settings.QuickActions[i] : "",
                PlaceholderText = i == 0 ? "z. B. Als Arztbrief formulieren" : "",
            };
            quickTable.Controls.Add(_quick[i], 1, i);
        }
        quick.Controls.Add(quickTable);
        quick.Controls.Add(Hint("Leere Felder werden ausgelassen, die folgenden rücken nach."));
        var quickReset = new LinkLabel { Text = "Vorschläge wiederherstellen", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        quickReset.LinkClicked += (_, _) =>
        {
            for (int i = 0; i < _quick.Length; i++)
                _quick[i].Text = i < Core.QuickActionList.Defaults.Count ? Core.QuickActionList.Defaults[i] : "";
        };
        quick.Controls.Add(quickReset);

        // ================= page: Vorlage =================
        var templateHint = Hint("So soll ein Text aussehen, der ohne Markierung geschrieben wird: eine Behandlungsdokumentation, "
            + "ein Angebot, eine Mail. Was in [eckigen Klammern] steht, füllt das Modell nur, wenn es diktiert wurde. "
            + "Leer lassen = ohne Vorlage.");
        _template = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = true, Margin = new Padding(0, 8, 0, 0),
            Text = (settings.PromptSystemPrompt ?? "").ReplaceLineEndings("\r\n"),
        };
        var samples = new Button { Text = "Beispiel einsetzen ▾", AutoSize = true, MinimumSize = new Size(0, 28), Anchor = AnchorStyles.Right };
        var sampleMenu = new ContextMenuStrip();
        sampleMenu.Items.Add("Praxis: Anamnese, Befund, Therapie", null, (_, _) => ReplaceTemplate(QuoteTemplate.Praxis));
        sampleMenu.Items.Add("Handwerk: Angebot", null, (_, _) => ReplaceTemplate(QuoteTemplate.Default));
        sampleMenu.Items.Add(new ToolStripSeparator());
        sampleMenu.Items.Add("Leeren (ohne Vorlage)", null, (_, _) => ReplaceTemplate(""));
        samples.Click += (_, _) => sampleMenu.Show(samples, new Point(0, samples.Height));
        NewFillPage(TemplatePage, templateHint, samples, _template, null);

        // ================= frame: navigation, page host, buttons =================
        var nav = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = NavBack, Padding = new Padding(8, 12, 8, 8) };
        var appTitle = new Label
        {
            Text = "WhisperShortcut", AutoSize = false, Location = new Point(18, 16), Size = new Size(180, 26),
            Font = new Font("Segoe UI Semibold", 11f), BackColor = NavBack,
        };
        nav.Controls.Add(appTitle);
        int y = 56;
        foreach (var name in new[] { General, Keys_, GlossaryPage, QuickPage, TemplatePage })
        {
            var item = new NavItem(name) { Location = new Point(8, y), Size = new Size(194, 36), TabStop = false };
            item.Click += (_, _) => ShowPage(name);
            _nav.Add(item);
            nav.Controls.Add(item);
            y += 40;
        }

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        foreach (var page in _pages.Values) _pageHost.Controls.Add(page);

        var save = new Button { Text = "Speichern", AutoSize = true, MinimumSize = new Size(108, 32), TabIndex = 0 };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, MinimumSize = new Size(108, 32), TabIndex = 1 };
        save.Click += (_, _) => Save();
        // The window is modeless (Show, not ShowDialog), so a DialogResult alone would not close it.
        cancel.Click += (_, _) => Close();
        _dirty = new Label { Text = "Nicht gespeicherte Änderungen", AutoSize = true, ForeColor = HintColor, Visible = false, Margin = new Padding(0, 9, 12, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true,
            Padding = new Padding(24, 10, 24, 14), BackColor = Color.White,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(_dirty);
        CancelButton = cancel;
        AcceptButton = save; // Enter in the key field saves; the multi-line boxes keep their Enter

        var content = new Panel { Dock = DockStyle.Fill };
        // Docking runs in reverse order of adding: buttons take the bottom, then the line, the page the rest.
        content.Controls.Add(_pageHost);
        content.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(229, 229, 229) });
        content.Controls.Add(buttons);
        Controls.Add(content);
        Controls.Add(nav);
        ResumeLayout(false);
        PerformLayout();

        // Hints wrap at the visible page width, never wider.
        _pageHost.ClientSizeChanged += (_, _) => FitHints();

        Load += (_, _) =>
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            FitHints();
            _snapshot = Snapshot();
        };
        UpdateMode();
        UpdateModelRows();
        ShowPage(General);
        Shown += (_, _) =>
        {
            (string.IsNullOrEmpty(_apiKey.Text) && _onlineMode.Checked ? (Control)_apiKey : save).Focus();
            if (_onlineMode.Checked && _apiKey.Text.Trim().Length > 0) _ = CheckKeyAsync();
        };

        // Any edit: show the "not saved" hint next to the buttons.
        var dirtyTimer = new System.Windows.Forms.Timer { Interval = 400 };
        dirtyTimer.Tick += (_, _) => _dirty.Visible = _snapshot.Length > 0 && Snapshot() != _snapshot;
        dirtyTimer.Start();
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            dirtyTimer.Dispose();
            _keyDebounce.Dispose();
            _keyCheck?.Cancel();
            StopMicTest();
            _meterTimer.Dispose();
        };
    }

    /// <summary>Opens one of the pages (<see cref="General"/>, <see cref="Keys_"/>, …).</summary>
    public void ShowPage(string name)
    {
        if (!_pages.TryGetValue(name, out var page)) return;
        foreach (var p in _pages.Values) p.Visible = p == page;
        foreach (var n in _nav) n.Selected = n.Text == name;
        if (name != General) StopMicTest();
        FitHints();
    }

    // ---------------- pages ----------------

    /// <summary>A scrolling page: title, then controls stacked top to bottom.</summary>
    private TableLayoutPanel NewPage(string title)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, BackColor = Color.White };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(32, 20, 32, 16), ColumnCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(PageTitle(title));
        // Rows are added as controls come in; AutoSize rows keep the stack tight.
        root.ControlAdded += (_, _) => root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scroll.Controls.Add(root);
        _pages[title] = scroll;
        return root;
    }

    /// <summary>A page whose editor fills the height (glossary, template): no scroll, the text box grows.</summary>
    private void NewFillPage(string title, Control hint, Control? headerAction, Control editor, Control? footer)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(32, 20, 32, 16), Visible = false, BackColor = Color.White };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(PageTitle(title), 0, 0);
        if (headerAction != null) head.Controls.Add(headerAction, 1, 0);
        root.Controls.Add(head);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(editor);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (footer != null)
        {
            root.Controls.Add(footer);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        _pages[title] = root;
    }

    private static void AddKeyRow(TableLayoutPanel table, int row, string title, string subtitle, HotkeyBox box)
    {
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 16, 6) };
        text.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0) });
        text.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = HintColor, Margin = new Padding(0) });
        table.Controls.Add(text, 0, row);
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Dock = DockStyle.None;
        table.Controls.Add(box, 1, row);
    }

    private static void AddFixedKey(TableLayoutPanel table, int row, string key, string what)
    {
        table.Controls.Add(new Label { Text = key, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, 4, 12, 4) }, 0, row);
        table.Controls.Add(new Label { Text = what, AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 1, row);
    }

    /// <summary>Text box (or combo) filling the width, buttons at its right.</summary>
    private static TableLayoutPanel Row(Control fill, params Control[] right)
    {
        var row = new TableLayoutPanel { ColumnCount = 1 + right.Length, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(fill, 0, 0);
        for (int i = 0; i < right.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(right[i], i + 1, 0);
        }
        return row;
    }

    private void ReplaceTemplate(string text)
    {
        if (_template.Text.Trim().Length > 0 && _template.Text.Trim() != text.Trim()
            && MessageBox.Show(this, "Die jetzige Vorlage wird ersetzt.", "Vorlage ersetzen?",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _template.Text = text.ReplaceLineEndings("\r\n");
    }

    private void UpdateGlossaryCount()
    {
        int n = _glossary.Lines.Count(l => l.Trim().Length > 0);
        _glossaryCount.Text = n switch { 0 => "Noch keine Begriffe", 1 => "1 Begriff", _ => $"{n} Begriffe" };
    }

    // ---------------- API key ----------------

    private async Task CheckKeyAsync()
    {
        _keyCheck?.Cancel();
        var cts = _keyCheck = new CancellationTokenSource();
        string key = _apiKey.Text.Trim();
        if (key.Length == 0)
        {
            SetKeyStatus("", HintColor);
            return;
        }
        SetKeyStatus("Wird geprüft …", HintColor);
        Core.KeyCheck result;
        try { result = await _gemini.CheckKeyAsync(key, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || IsDisposed) return;
        switch (result)
        {
            case Core.KeyCheck.Ok: SetKeyStatus("✓ Schlüssel funktioniert", Good); break;
            case Core.KeyCheck.Invalid: SetKeyStatus("✗ Google kennt diesen Schlüssel nicht. Bitte noch einmal kopieren, ohne Leerzeichen.", Bad); break;
            case Core.KeyCheck.NoConnection: SetKeyStatus("Keine Verbindung zu Google. Internet prüfen; gespeichert werden kann trotzdem.", Caution); break;
            default: SetKeyStatus("Google hat nicht eindeutig geantwortet. Später noch einmal prüfen.", Caution); break;
        }
    }

    private void SetKeyStatus(string text, Color color)
    {
        _keyStatus.Text = text;
        _keyStatus.ForeColor = color;
        _keyStatus.Visible = _onlineMode.Checked && text.Length > 0;
    }

    // ---------------- microphone ----------------

    private sealed record MicItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private void FillMicrophones(string? selected)
    {
        _mic.Items.Add(new MicItem("", "Windows-Standard"));
        foreach (var name in Microphones.Names()) _mic.Items.Add(new MicItem(name, name));
        var items = _mic.Items.Cast<MicItem>().ToList();
        var match = items.FirstOrDefault(i => i.Value == (selected ?? ""));
        if (match == null)
        {
            // Headset unplugged: keep the choice instead of silently switching to the default.
            match = new MicItem(selected!, $"{selected} (nicht angeschlossen, solange gilt Windows-Standard)");
            _mic.Items.Add(match);
        }
        _mic.SelectedItem = match;
    }

    private string SelectedMicrophone => ((MicItem)_mic.SelectedItem!).Value;

    private void ToggleMicTest()
    {
        if (_probe != null)
        {
            StopMicTest();
            return;
        }
        try
        {
            _probe = new LevelProbe(SelectedMicrophone);
            _probe.Start();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Mic test failed: {e.GetType().Name}");
            _probe?.Dispose();
            _probe = null;
            Warn(e is MicrophoneUnavailableException ? e.Message : "Das Mikrofon lässt sich nicht öffnen: " + e.Message);
            return;
        }
        _micTest.Text = "Stopp";
        _micMeter.Level = 0;
        _micMeter.Visible = true;
        _meterTimer.Start();
    }

    private void StopMicTest()
    {
        _meterTimer.Stop();
        _probe?.Dispose();
        _probe = null;
        if (IsDisposed) return;
        _micTest.Text = "Testen";
        _micMeter.Visible = false;
    }

    // ---------------- offline models ----------------

    private Core.LocalLlm SelectedLlmChoice => ((LlmItem)_llmChoice.SelectedItem!).Llm;

    /// <summary>Online shows the key, offline shows the models. Hidden, not greyed out: one way of working at a time.</summary>
    private void UpdateMode()
    {
        SuspendLayout();
        foreach (var c in _onlineOnly) c.Visible = _onlineMode.Checked;
        foreach (var c in _offlineOnly) c.Visible = _offlineMode.Checked;
        _keyStatus.Visible = _onlineMode.Checked && _keyStatus.Text.Length > 0;
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
            row.SetStatus($"✓ Geladen ({FormatBytes(size)} auf diesem PC)", Good);
        }
        else if (error != null)
        {
            row.SetStatus(error, Bad);
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

    private void FitHints()
    {
        // Page padding (64 at 96 dpi) and a possible scroll bar.
        int width = _pageHost.ClientSize.Width - LogicalToDeviceUnits(64) - SystemInformation.VerticalScrollBarWidth;
        width = Math.Max(width, LogicalToDeviceUnits(240));
        foreach (var hint in _hints) hint.MaximumSize = new Size(width, 0);
    }

    // ---------------- save / close ----------------

    /// <summary>Everything the user can change, as one string: equal = nothing to save.</summary>
    private string Snapshot() => string.Join("\u0001", new[]
    {
        _onlineMode.Checked.ToString(), SelectedLlmChoice.Id, _apiKey.Text.Trim(), SelectedMicrophone,
        _dictateKey.Value, _promptKey.Value, _glossaryKey.Value, _autoStart.Checked.ToString(),
        _glossary.Text, _template.Text,
    }.Concat(_quick.Select(q => q.Text)));

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_saved || e.CloseReason != CloseReason.UserClosing || Snapshot() == _snapshot) return;
        var answer = MessageBox.Show(this, "Die Änderungen sind noch nicht gespeichert.", "Änderungen speichern?",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) e.Cancel = true;
        else if (answer == DialogResult.Yes && !TrySave()) e.Cancel = true;
    }

    private void Save()
    {
        if (TrySave()) Close();
    }

    private bool TrySave()
    {
        if (!Hotkey.TryParse(_dictateKey.Value, out var dictate) || !Hotkey.TryParse(_promptKey.Value, out var prompt)
            || !Hotkey.TryParse(_glossaryKey.Value, out var glossary)
            || !IsSafe(dictate) || !IsSafe(prompt) || !IsSafe(glossary))
        {
            ShowPage(Keys_);
            Warn("Bitte für jede Funktion ein Tastenkürzel mit zwei Zusatztasten festlegen, z. B. Strg+Umschalt+D.");
            return false;
        }
        if (dictate == prompt || dictate == glossary || prompt == glossary)
        {
            ShowPage(Keys_);
            Warn("Die Tastenkürzel müssen verschieden sein.");
            return false;
        }

        _settings.OfflineMode = _offlineMode.Checked;
        _settings.OfflineLlmId = SelectedLlmChoice.Id;
        _settings.ApiKey = _apiKey.Text.Trim();
        _settings.Microphone = SelectedMicrophone;
        _settings.DictateHotkey = dictate.ToString();
        _settings.PromptHotkey = prompt.ToString();
        _settings.GlossaryHotkey = glossary.ToString();
        _settings.Glossary = _glossary.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        _settings.QuickActions = Core.QuickActionList.Clean(_quick.Select(q => q.Text));
        _settings.PromptSystemPrompt = _template.Text.Trim().Length == 0 ? "" : _template.Text.ReplaceLineEndings("\n").Trim();
        try
        {
            _settings.Save();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Saving settings failed: {e.GetType().Name}");
            Warn("Die Einstellungen konnten nicht gespeichert werden: " + e.Message);
            return false;
        }
        if (_autoStart.Checked != AutoStart.IsEnabled)
        {
            if (!(_autoStart.Checked ? AutoStart.Enable() : AutoStart.Disable()))
                Warn("Der automatische Start konnte nicht geändert werden. Details stehen im Protokoll.");
        }
        Core.Log.Info($"Settings saved (autostart {AutoStart.IsEnabled}, offline {_settings.OfflineMode}, llm {_settings.OfflineLlmId}, key set: {_settings.ApiKey.Length > 0}, mic {(_settings.Microphone.Length > 0 ? _settings.Microphone : "default")}, glossary {_settings.Glossary.Count}, quick actions {_settings.QuickActions.Count}, template {_settings.PromptSystemPrompt.Length} chars)");
        _saved = true;
        DialogResult = DialogResult.OK;
        return true;
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

    private static Label PageTitle(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Margin = new Padding(0, 0, 0, 6),
    };

    private static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 18, 0, 4),
    };

    private Label Hint(string text)
    {
        var hint = new Label
        {
            Text = text, AutoSize = true, ForeColor = HintColor, Margin = new Padding(0, 4, 0, 4),
            MaximumSize = new Size(560, 0), // replaced by FitHints once the real width is known
        };
        _hints.Add(hint);
        return hint;
    }

    private Label Banner(string text)
    {
        var banner = Hint(text);
        banner.ForeColor = Color.FromArgb(0, 51, 102);
        banner.BackColor = Color.FromArgb(229, 241, 251);
        banner.Padding = new Padding(12, 10, 12, 10);
        banner.Margin = new Padding(0, 4, 0, 4);
        return banner;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening URL failed: {e.GetType().Name}"); }
    }

    /// <summary>A page entry on the left, drawn like the Windows 11 settings (grey pill, blue bar when selected).</summary>
    private sealed class NavItem : Button
    {
        private bool _selected, _hover;

        public NavItem(string text)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AccessibleRole = AccessibleRole.PageTab;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => _selected;
            set { _selected = value; AccessibleDescription = value ? "ausgewählt" : null; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(NavBack);
            var r = new RectangleF(0.5f, 2.5f, Width - 1, Height - 5);
            if (_selected || _hover)
            {
                using var fill = new SolidBrush(_selected ? Color.FromArgb(233, 233, 233) : Color.FromArgb(237, 237, 237));
                using var path = Rounded(r, LogicalToDeviceUnits(5));
                g.FillPath(fill, path);
            }
            if (_selected)
            {
                float barH = Height * 0.42f, barW = LogicalToDeviceUnits(3);
                using var bar = new SolidBrush(Accent);
                using var path = Rounded(new RectangleF(r.X + 1, (Height - barH) / 2, barW, barH), barW / 2);
                g.FillPath(bar, path);
            }
            var font = _selected ? new Font(Font, FontStyle.Bold) : Font;
            TextRenderer.DrawText(g, Text, font, new Rectangle(LogicalToDeviceUnits(16), 0, Width - LogicalToDeviceUnits(20), Height),
                ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (_selected) font.Dispose();
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
        }
    }

    /// <summary>Microphone level for the "Testen" button: grey track, green fill, quick rise and slow fall.</summary>
    private sealed class LevelMeter : Control
    {
        private float _shown;

        public LevelMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float Level
        {
            set
            {
                // Speech peaks are short; falling slowly keeps the bar readable.
                float v = Math.Clamp(value * 2.5f, 0, 1);
                _shown = v > _shown ? v : Math.Max(v, _shown - 0.04f);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.White);
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var back = new SolidBrush(Color.FromArgb(225, 225, 225)))
            using (var path = Rounded(track, Height / 2f))
                g.FillPath(back, path);
            if (_shown <= 0.01f) return;
            var fill = new RectangleF(0, 0, Math.Max(Height, (Width - 1) * _shown), Height - 1);
            using var green = new SolidBrush(Good);
            using var fillPath = Rounded(fill, Height / 2f);
            g.FillPath(green, fillPath);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
