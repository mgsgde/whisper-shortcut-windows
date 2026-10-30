using System.Diagnostics;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// Settings window in the style of the Windows 11 settings, ordered like the macOS app: pages on the
/// left (Allgemein, Diktieren, Dictate Prompt, Glossar, Offline, Info), the page on the right as
/// rounded cards, Speichern/Abbrechen below. Light or dark follows Windows. German UI.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly Core.Settings _settings;
    private readonly OfflineEngine _offline;
    private readonly Core.GeminiClient _gemini;
    private readonly Core.OpenAiClient _openAi;
    private readonly OptionButton _onlineMode;
    private readonly OptionButton _offlineMode;
    private readonly Control[] _offlineOnly;
    private readonly ModelRow _speechRow;
    private readonly ModelRow _llmRow;
    private readonly FluentCombo _llmChoice;
    private readonly ThemedLabel _offlineSummary;
    private readonly ThemedLabel _offlineState;
    private readonly KeyField _geminiKey;
    private readonly KeyField _openAiKey;
    private readonly FluentCombo _speechEngine;
    private readonly FluentCombo _textEngine;
    private readonly FluentCombo _dictateModelOpenAi;
    private readonly FluentCombo _promptModelOpenAi;
    private readonly Control[] _engineOnly;
    private readonly Card _dictateGeminiCard, _dictateOpenAiCard, _promptGeminiCard, _promptOpenAiCard;
    private readonly FluentCombo _mic;
    private readonly FluentButton _micTest;
    private readonly Bar _micMeter;
    private readonly HotkeyBox _dictateKey;
    private readonly HotkeyBox _promptKey;
    private readonly HotkeyBox _glossaryKey;
    private readonly FluentCombo _dictateModel;
    private readonly FluentCombo _promptModel;
    private readonly PromptEditor _dictatePrompt;
    private readonly PromptEditor _promptPrompt;
    private readonly ToggleSwitch _autoStart;
    private readonly ToggleSwitch _autoPaste;
    private readonly ToggleSwitch _restoreClipboard;
    private readonly ToggleSwitch _sounds;
    private readonly Banner _welcome;
    private ThemedLabel _updateStatus = null!;
    private FluentButton _updateButton = null!;
    private readonly UpdateService _updates;
    private ToggleSwitch _autoUpdate = null!;
    private readonly TextBox _glossary;
    private readonly ThemedLabel _glossaryCount;
    private readonly TextBox _template;
    private readonly TextBox[] _quick = new TextBox[Core.QuickActionList.Max];
    private readonly ContextMenuStrip _sampleMenu;
    private readonly Core.PromptHistory _history = Core.PromptHistory.Load(Core.PromptHistory.DefaultPath);
    private readonly HistoryLink _dictateHistory, _promptHistory, _templateHistory;
    private readonly ThemedLabel _dirty;
    private readonly List<(Label Label, int Inset)> _wraps = new();
    private readonly List<NavItem> _nav = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Panel _pageHost;
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    private LevelProbe? _probe;
    private string _snapshot = "";
    private bool _loadedOffline;
    private readonly FluentButton _close;
    // "✓ Gespeichert" stays next to the buttons until then (Environment.TickCount64).
    private long _savedHintUntil;

    /// <summary>Raised after the settings were written; the window stays open.</summary>
    public event Action? Saved;
    private bool _discard;

    public const string General = "Allgemein", DictatePage = "Diktieren", PromptPage = "Text bearbeiten",
        GlossaryPage = "Glossar", OfflinePage = "Offline", InfoPage = "Info";

    // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs for the navigation.
    private static readonly (string Name, string Icon)[] Pages =
    {
        (General, ""), (DictatePage, ""), (PromptPage, ""),
        (GlossaryPage, ""), (OfflinePage, ""), (InfoPage, ""),
    };

    // Inset (96 dpi) of a description inside a card row with a control of this width on the right.
    private const int RowPadding = 16, RowGap = 24;
    private const int KeyWidth = 200, ComboWidth = 300, ToggleWidth = 88, ButtonWidth = 120;

    /// <param name="welcome">First start: a short "what to do now" box on top of the first page.</param>
    public SettingsForm(Core.Settings settings, OfflineEngine offline, Core.GeminiClient gemini, Core.OpenAiClient openAi, UpdateService updates, bool welcome = false)
    {
        _settings = settings;
        _updates = updates;
        _offline = offline;
        _gemini = gemini;
        _openAi = openAi;

        // Designer pattern: all sizes below are written for 96 dpi and scaled once on ResumeLayout,
        // so 125/150/200 % get the same layout instead of big text in a small window.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Einstellungen";
        Font = Theme.Body;
        StartPosition = FormStartPosition.Manual; // centered in Load, after the size is known
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(760, 520);
        MaximizeBox = false;
        ShowInTaskbar = true;
        Icon = TrayIcons.App;

        // ================= page: Allgemein =================
        var general = NewPage(General, null);
        {
            _welcome = new Banner
            {
                Text = "Willkommen! Zuerst die Arbeitsweise wählen: Online braucht einen Schlüssel von Google (Gemini) oder OpenAI, "
                    + "offline werden einmal die Modelle geladen. Dann „Speichern“. Danach in einem beliebigen Programm "
                    + $"{Hotkey.Display(settings.DictateHotkey)} drücken, sprechen und noch einmal drücken.",
                AutoSize = true, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 4, 0, 4), Visible = welcome,
            };
            Fit(_welcome, 0);
            general.Controls.Add(_welcome);
        }
        general.Controls.Add(Section("Arbeitsweise", first: true));
        _onlineMode = new OptionButton("Online") { Checked = !settings.OfflineMode };
        _offlineMode = new OptionButton("Offline auf diesem PC") { Checked = settings.OfflineMode };
        // Each option sits in its own card row, so the automatic radio grouping (same parent) does not apply.
        _onlineMode.CheckedChanged += (_, _) => { if (_onlineMode.Checked) _offlineMode.Checked = false; };
        _offlineMode.CheckedChanged += (_, _) => { if (_offlineMode.Checked) _onlineMode.Checked = false; };
        var modeCard = new Card();
        modeCard.AddRow(OptionRow(_onlineMode, "Beste Qualität und am schnellsten. Braucht Internet und einen API-Schlüssel von Google (Gemini) "
            + "oder OpenAI; Aufnahme und markierter Text gehen an den gewählten Anbieter. Einzelne Schritte können auch auf diesem PC laufen."));
        modeCard.AddRow(OptionRow(_offlineMode, "Aufnahmen und Texte verlassen den Rechner nie, richtig für Patientendaten. Die Modelle werden einmal "
            + "geladen, danach geht alles ohne Internet. „Text bearbeiten“ dauert offline länger, je nach PC einige Sekunden bis eine Minute."));
        general.Controls.Add(modeCard);

        // --- engines (online): chosen per step, so speech recognition can stay on this PC while texts go online ---
        var engineSection = Section("Anbieter");
        general.Controls.Add(engineSection);
        _speechEngine = EngineChoice(settings.SpeechEngine, "Spracherkennung", "Auf diesem PC (Parakeet)");
        _textEngine = EngineChoice(settings.TextEngine, "Texte bearbeiten", "Auf diesem PC (Sprachmodell)");
        var engineCard = new Card();
        engineCard.AddRow(SettingRow("Spracherkennung", "Diktieren, und die gesprochene Anweisung bei „Text bearbeiten“.", _speechEngine, ComboWidth, out _));
        engineCard.AddRow(SettingRow("Texte bearbeiten", "Markierungen ändern, neue Texte schreiben, Schnellanweisungen.", _textEngine, ComboWidth, out _));
        var engineNote = Block();
        engineNote.Controls.Add(Wrap($"Gemischt geht auch: Die Spracherkennung auf diesem PC braucht nur {FormatBytes(ComponentSize(Core.OfflineCatalog.Parakeet))} "
            + "und ist sehr schnell, die Texte gehen online. Das große Sprachmodell muss dafür nicht geladen werden.", 2 * RowPadding, small: true));
        engineCard.AddRow(engineNote);
        general.Controls.Add(engineCard);
        _engineOnly = new Control[] { engineSection, engineCard };

        // --- API keys (shown for the providers in use) ---
        _geminiKey = new KeyField(settings.ApiKey ?? "", (k, ct) => _gemini.CheckKeyAsync(k, ct), result => result switch
        {
            Core.KeyCheck.Ok => ("✓ Schlüssel funktioniert", Tone.Good),
            Core.KeyCheck.Invalid => ("✗ Google kennt diesen Schlüssel nicht. Bitte noch einmal kopieren, ohne Leerzeichen.", Tone.Bad),
            Core.KeyCheck.Forbidden => ("✗ Google kennt den Schlüssel, lässt ihn aber nicht an Gemini. In Google AI Studio einen neuen Schlüssel erstellen.", Tone.Bad),
            Core.KeyCheck.NoConnection => ("Keine Verbindung zu Google. Internet prüfen; gespeichert werden kann trotzdem.", Tone.Caution),
            _ => ("Google hat nicht eindeutig geantwortet. Später noch einmal prüfen.", Tone.Caution),
        });
        AddKeyCard(general, _geminiKey, "Gemini-API-Schlüssel", "Bitte einen Schlüssel aus einem Google-Projekt mit Abrechnung verwenden: "
            + "Im kostenlosen Kontingent darf Google die Aufnahmen zum Training nutzen.",
            "Schlüssel in Google AI Studio erstellen", "https://aistudio.google.com/apikey");
        _openAiKey = new KeyField(settings.OpenAiKey ?? "", (k, ct) => _openAi.CheckKeyAsync(k, ct), result => result switch
        {
            Core.KeyCheck.Ok => ("✓ Schlüssel funktioniert", Tone.Good),
            Core.KeyCheck.Invalid => ("✗ OpenAI kennt diesen Schlüssel nicht. Bitte noch einmal kopieren, ohne Leerzeichen.", Tone.Bad),
            Core.KeyCheck.Forbidden => ("✗ OpenAI kennt den Schlüssel, er darf aber keine Modelle benutzen. Rechte des Projekts prüfen.", Tone.Bad),
            Core.KeyCheck.NoConnection => ("Keine Verbindung zu OpenAI. Internet prüfen; gespeichert werden kann trotzdem.", Tone.Caution),
            _ => ("OpenAI hat nicht eindeutig geantwortet. Später noch einmal prüfen.", Tone.Caution),
        });
        AddKeyCard(general, _openAiKey, "OpenAI-API-Schlüssel", "Ein API-Schlüssel mit Guthaben auf platform.openai.com. "
            + "Ein ChatGPT-Abo zählt dafür nicht.",
            "Schlüssel bei OpenAI erstellen", "https://platform.openai.com/api-keys");
        _speechEngine.SelectedIndexChanged += (_, _) => UpdateMode();
        _textEngine.SelectedIndexChanged += (_, _) => UpdateMode();

        // --- offline models: summary here, the downloads on the Offline page ---
        var offlineSection = Section("Offline-Modelle");
        general.Controls.Add(offlineSection);
        var manage = new FluentButton("Verwalten") { MinimumSize = new Size(ButtonWidth, 32) };
        manage.Click += (_, _) => ShowPage(OfflinePage);
        var offlineCard = new Card();
        offlineCard.AddRow(SettingRow("Spracherkennung und Sprachmodell", "", manage, ButtonWidth, out _offlineSummary));
        general.Controls.Add(offlineCard);
        _offlineOnly = new Control[] { offlineSection, offlineCard };

        // --- microphone ---
        general.Controls.Add(Section("Mikrofon"));
        _mic = new FluentCombo { Dock = DockStyle.Fill };
        FillMicrophones(settings.Microphone);
        _micTest = new FluentButton("Testen") { MinimumSize = new Size(100, 32) };
        var micBlock = Block();
        micBlock.Controls.Add(Row(_mic, _micTest));
        _micMeter = new Bar(level: true) { Margin = new Padding(0, 8, 0, 4), Visible = false };
        micBlock.Controls.Add(_micMeter);
        micBlock.Controls.Add(Wrap("„Windows-Standard“ folgt dem Gerät, das in Windows unter System → Sound als Eingabe gewählt ist. "
            + "Ein Headset ist in der Praxis deutlich besser als das eingebaute Mikrofon.", 2 * RowPadding, small: true));
        var micCard = new Card();
        micCard.AddRow(micBlock);
        general.Controls.Add(micCard);
        _micTest.Click += (_, _) => ToggleMicTest();
        _mic.SelectedIndexChanged += (_, _) => { if (!_refillingMics && _probe != null) { StopMicTest(); ToggleMicTest(); } };
        // A headset plugged in while the window is open shows up when the list is opened.
        _mic.DropDown += (_, _) => RefreshMicrophones();
        _meterTimer.Tick += (_, _) => _micMeter.Value = _probe?.Level ?? 0;

        // --- pasting ---
        general.Controls.Add(Section("Einfügen"));
        _autoPaste = new ToggleSwitch { Checked = settings.AutoPaste, AccessibleName = "Automatisch einfügen" };
        _restoreClipboard = new ToggleSwitch { Checked = settings.RestoreClipboard, AccessibleName = "Zwischenablage wiederherstellen" };
        var pasteCard = new Card();
        pasteCard.AddRow(SettingRow("Automatisch einfügen", "Der Text erscheint dort, wo der Cursor steht. Aus: Er liegt nur in der "
            + "Zwischenablage und wird mit Strg+V selbst eingefügt.", _autoPaste, ToggleWidth, out _));
        pasteCard.AddRow(SettingRow("Zwischenablage wiederherstellen", "Nach dem Einfügen kommt zurück, was vorher in der Zwischenablage war.",
            _restoreClipboard, ToggleWidth, out _));
        general.Controls.Add(pasteCard);
        // Without pasting nothing is restored: the text itself stays in the clipboard.
        _autoPaste.CheckedChanged += (_, _) => _restoreClipboard.Enabled = _autoPaste.Checked;
        _restoreClipboard.Enabled = _autoPaste.Checked;

        // --- sounds ---
        general.Controls.Add(Section("Töne"));
        _sounds = new ToggleSwitch { Checked = settings.Sounds, AccessibleName = "Signaltöne" };
        var soundCard = new Card();
        soundCard.AddRow(SettingRow("Signaltöne", "Ein kurzer Ton beim Start und Ende einer Aufnahme und bei Fehlern. "
            + "Aus: Nur das Symbol und die Anzeige am Bildschirm zeigen, was gerade passiert.", _sounds, ToggleWidth, out _));
        general.Controls.Add(soundCard);

        // --- start ---
        general.Controls.Add(Section("Start"));
        _autoStart = new ToggleSwitch { Checked = AutoStart.IsEnabled, AccessibleName = "Mit Windows starten" };
        var startCard = new Card();
        startCard.AddRow(SettingRow("Mit Windows starten", "Dann ist WhisperShortcut nach jedem Neustart gleich unten rechts im Infobereich da.",
            _autoStart, ToggleWidth, out _));
        general.Controls.Add(startCard);

        // --- keys during a recording ---
        general.Controls.Add(Section("Während einer Aufnahme"));
        var fixedCard = new Card();
        fixedCard.AddRow(SettingRow("Aufnahme verwerfen oder Verarbeitung abbrechen", null, KeyCap("Esc"), ButtonWidth + 40, out _));
        fixedCard.AddRow(SettingRow("Schnellanweisung anwenden (nur bei „Text bearbeiten“)", null, KeyCap("Strg+1 bis Strg+5"), ButtonWidth + 40, out _));
        general.Controls.Add(fixedCard);
        general.Controls.Add(Wrap("Diese Tasten sind nur belegt, solange die Anzeige unten zu sehen ist. Sonst gehören sie wie gewohnt "
            + "den anderen Programmen.", 0));

        // ================= page: Diktieren =================
        var dictate = NewPage(DictatePage, "Taste drücken, sprechen, noch einmal drücken: Der Text steht dort, wo der Cursor blinkt.");
        _dictateKey = new HotkeyBox(settings.DictateHotkey) { AccessibleName = "Tastenkürzel Diktieren" };
        dictate.Controls.Add(KeyCard(_dictateKey, "Ins Feld klicken und die neue Kombination drücken, z. B. Strg+Umschalt+D. "
            + "Solange dieses Fenster im Vordergrund ist, sind die Kürzel aus."));
        dictate.Controls.Add(Section("Modell"));
        _dictateModel = ModelChoice(Core.GeminiModels.Transcription, settings.TranscriptionModel);
        dictate.Controls.Add(_dictateGeminiCard = ModelCard(_dictateModel, "Gemini-Modell", "Gilt, wenn die Spracherkennung über Gemini läuft "
            + "(Allgemein → Anbieter). Auf diesem PC erkennt Parakeet die Sprache, ohne Modellwahl und ohne eigene Anweisungen."));
        _dictateModelOpenAi = ModelChoice(Core.OpenAiModels.Transcription, settings.OpenAiTranscriptionModel);
        dictate.Controls.Add(_dictateOpenAiCard = ModelCard(_dictateModelOpenAi, "OpenAI-Modell", "Gilt, wenn die Spracherkennung über OpenAI läuft. "
            + "Das Glossar geht als Hinweis mit; die Anweisungen unten gelten nur für Gemini."));
        dictate.Controls.Add(Section("Anweisungen"));
        dictate.Controls.Add(Wrap("So soll Gemini transkribieren (gilt nur für Gemini): Füllwörter, Satzzeichen, Formatierung. Namen und Fachwörter gehören "
            + "ins Glossar, nicht hierher. Der Standardtext ist englisch; geschrieben wird trotzdem in der gesprochenen Sprache.", 0));
        _dictatePrompt = new PromptEditor(Core.Prompts.DefaultTranscriptionSystemPrompt, settings.DictateSystemPrompt, "Anweisungen Diktieren");
        _dictateHistory = new HistoryLink(_dictatePrompt.Box, _history, Core.PromptField.Dictate);
        dictate.Controls.Add(PromptCard(_dictatePrompt, _dictateHistory));

        // ================= page: Text bearbeiten (Dictate Prompt) =================
        var prompt = NewPage(PromptPage, "Text markieren, Kürzel drücken und sagen, was damit passieren soll („freundlicher“, "
            + "„ins Englische“), noch einmal drücken. Ohne Markierung schreibt das Modell einen neuen Text nach der Vorlage.");
        _promptKey = new HotkeyBox(settings.PromptHotkey) { AccessibleName = "Tastenkürzel Text bearbeiten" };
        prompt.Controls.Add(KeyCard(_promptKey, "Ins Feld klicken und die neue Kombination drücken, z. B. Strg+Umschalt+P. "
            + "Ein Druck während der Verarbeitung bricht ab."));
        prompt.Controls.Add(Section("Modell"));
        _promptModel = ModelChoice(Core.GeminiModels.Prompt, settings.PromptModel);
        prompt.Controls.Add(_promptGeminiCard = ModelCard(_promptModel, "Gemini-Modell", "Gilt, wenn Texte über Gemini bearbeitet werden "
            + "(Allgemein → Anbieter). Auf diesem PC arbeitet das Sprachmodell, das unter „Offline“ gewählt ist."));
        _promptModelOpenAi = ModelChoice(Core.OpenAiModels.Prompt, settings.OpenAiPromptModel);
        prompt.Controls.Add(_promptOpenAiCard = ModelCard(_promptModelOpenAi, "OpenAI-Modell", "Gilt, wenn Texte über OpenAI bearbeitet werden."));
        prompt.Controls.Add(Section("Anweisungen"));
        prompt.Controls.Add(Wrap("Grundregeln fürs Bearbeiten und Schreiben. Gelten immer: beim Bearbeiten einer Markierung, bei neuen Texten "
            + "und bei den Schnellanweisungen, online und offline. Hier hinein gehören feste Regeln wie Ton, Rechtschreibung oder "
            + "„nichts erfinden“. Der Standardtext ist englisch; geantwortet wird trotzdem in der Sprache des Textes.", 0));
        _promptPrompt = new PromptEditor(Core.Prompts.DefaultPromptModeSystemPrompt, settings.PromptModeSystemPrompt, "Anweisungen Text bearbeiten");
        _promptHistory = new HistoryLink(_promptPrompt.Box, _history, Core.PromptField.PromptMode);
        prompt.Controls.Add(PromptCard(_promptPrompt, _promptHistory));

        prompt.Controls.Add(Section("Vorlage für neue Texte"));
        prompt.Controls.Add(Wrap("So soll ein Text aussehen, der ohne Markierung geschrieben wird: eine Behandlungsdokumentation, "
            + "ein Angebot, eine Mail. Was in [eckigen Klammern] steht, füllt das Modell nur, wenn es diktiert wurde. "
            + "Leer lassen = ohne Vorlage.", 0));
        _template = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Text = (settings.DictatePromptTemplate ?? "").ReplaceLineEndings("\r\n"), AccessibleName = "Vorlage für neue Texte",
        };
        var samples = new FluentButton("Beispiel einsetzen ▾");
        _sampleMenu = new ContextMenuStrip();
        _sampleMenu.Items.Add("Praxis: Anamnese, Befund, Therapie", null, (_, _) => ReplaceTemplate(QuoteTemplate.Praxis));
        _sampleMenu.Items.Add("Werkstatt: Auftragsbericht", null, (_, _) => ReplaceTemplate(QuoteTemplate.Werkstatt));
        _sampleMenu.Items.Add("Handwerk: Angebot", null, (_, _) => ReplaceTemplate(QuoteTemplate.Default));
        _sampleMenu.Items.Add(new ToolStripSeparator());
        _sampleMenu.Items.Add("Leeren (ohne Vorlage)", null, (_, _) => ReplaceTemplate(""));
        samples.Click += (_, _) => { Theme.Menu(_sampleMenu); _sampleMenu.Show(samples, new Point(0, samples.Height)); };
        var templateBlock = Block();
        templateBlock.Controls.Add(new FieldFrame(_template, multiline: true) { Height = 260 });
        _templateHistory = new HistoryLink(_template, _history, Core.PromptField.Template);
        templateBlock.Controls.Add(Row(Wrap("Gilt nur, wenn nichts markiert ist. Regeln, die immer gelten sollen, gehören oben unter Anweisungen.",
            2 * RowPadding + 180, small: true), _templateHistory, samples));
        var templateCard = new Card();
        templateCard.AddRow(templateBlock);
        prompt.Controls.Add(templateCard);

        prompt.Controls.Add(Section("Schnellanweisungen"));
        prompt.Controls.Add(Wrap($"Erscheinen bei „Text bearbeiten“ ({Hotkey.Display(settings.PromptHotkey)}) unten in der Anzeige. "
            + "Strg+1 bis Strg+5 oder ein Klick wendet sie sofort an: auf die Markierung oder, ohne Markierung, auf das gerade "
            + "Diktierte. Beispiel: Text markieren, Strg+3, schon kürzer. Leere Felder werden ausgelassen.", 0));
        var quickTable = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        quickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        quickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < _quick.Length; i++)
        {
            quickTable.Controls.Add(new ThemedLabel { Text = $"Strg+{i + 1}", Tone = Tone.Secondary, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, i);
            _quick[i] = new TextBox
            {
                MaxLength = Core.QuickActionList.MaxLength, AccessibleName = $"Schnellanweisung Strg+{i + 1}",
                Text = i < settings.QuickActions.Count ? settings.QuickActions[i] : "",
                PlaceholderText = i == 0 ? "z. B. Als Arztbrief formulieren" : "",
            };
            quickTable.Controls.Add(new FieldFrame(_quick[i]) { Margin = new Padding(0, 3, 0, 3) }, 1, i);
        }
        var quickReset = new LinkLabel { Text = "Vorschläge wiederherstellen", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        quickReset.LinkClicked += (_, _) =>
        {
            for (int i = 0; i < _quick.Length; i++)
                _quick[i].Text = i < Core.QuickActionList.Defaults.Count ? Core.QuickActionList.Defaults[i] : "";
        };
        var quickBlock = Block();
        quickBlock.Controls.Add(quickTable);
        quickBlock.Controls.Add(quickReset);
        var quickCard = new Card();
        quickCard.AddRow(quickBlock);
        prompt.Controls.Add(quickCard);

        // ================= page: Glossar =================
        _glossaryKey = new HotkeyBox(settings.GlossaryHotkey) { AccessibleName = "Tastenkürzel Glossar" };
        _glossary = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Glossar",
            Text = string.Join("\r\n", settings.Glossary ?? new List<string>()),
        };
        _glossaryCount = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 4, 0, 0) };
        _glossary.TextChanged += (_, _) => UpdateGlossaryCount();
        UpdateGlossaryCount();
        NewFillPage(GlossaryPage,
            KeyCard(_glossaryKey, "Das richtig geschriebene Wort irgendwo markieren und das Kürzel drücken: Es steht dann in der Liste unten."),
            Wrap("Ein Begriff pro Zeile: Namen, Medikamente, Fachwörter, die richtig geschrieben werden sollen. "
                + "Gilt auch offline; dort braucht die Spracherkennung nach einer Änderung einige Sekunden.", 0),
            new FieldFrame(_glossary, multiline: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) },
            _glossaryCount);

        // ================= page: Offline =================
        var offlinePage = NewPage(OfflinePage, null);
        _offlineState = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 0, 0, 4) };
        Fit(_offlineState, 0);
        offlinePage.Controls.Add(_offlineState);
        var speech = Core.OfflineCatalog.Parakeet;
        offlinePage.Controls.Add(Section("Spracherkennung"));
        _speechRow = new ModelRow("Parakeet", "Für Diktieren und für gesprochene Anweisungen. Versteht Deutsch, Englisch und weitere Sprachen.", this);
        var speechCard = new Card();
        speechCard.AddRow(_speechRow);
        offlinePage.Controls.Add(speechCard);
        offlinePage.Controls.Add(Section("Sprachmodell für „Text bearbeiten“"));
        _llmChoice = new FluentCombo { Dock = DockStyle.Top, AccessibleName = "Sprachmodell" };
        var recommended = Core.OfflineCatalog.Recommended(OfflineEngine.TotalRam);
        foreach (var llm in Core.OfflineCatalog.Llms) _llmChoice.Items.Add(new LlmItem(llm, llm.Id == recommended.Id));
        _llmChoice.SelectedItem = _llmChoice.Items.Cast<LlmItem>().First(i => i.Llm.Id == offline.SelectedLlm.Id);
        var llmBlock = Block();
        llmBlock.Controls.Add(_llmChoice);
        llmBlock.Controls.Add(Wrap($"Dieser PC hat {FormatBytes(OfflineEngine.TotalRam, binary: true)} Arbeitsspeicher. "
            + "Das größere Modell schreibt bessere Texte, braucht aber mehr Speicher und Zeit.", 2 * RowPadding, small: true));
        _llmRow = new ModelRow("Gewähltes Sprachmodell", null, this);
        var llmCard = new Card();
        llmCard.AddRow(llmBlock);
        llmCard.AddRow(_llmRow);
        offlinePage.Controls.Add(llmCard);
        offlinePage.Controls.Add(Wrap(@"Das Laden läuft weiter, wenn das Fenster zu ist. Die Modelle liegen unter %LOCALAPPDATA%\WhisperShortcut\Offline.", 0));
        _speechRow.Action.Click += (_, _) => ToggleDownload("speech", new[] { speech });
        _speechRow.Remove.LinkClicked += (_, _) => RemoveModel(speech, "die Offline-Spracherkennung");
        _llmRow.Action.Click += (_, _) => ToggleDownload("llm",
            new[] { Core.OfflineCatalog.LlamaServer(OfflineEngine.Arch), SelectedLlmChoice.Component });
        _llmRow.Remove.LinkClicked += (_, _) => RemoveModel(SelectedLlmChoice.Component, SelectedLlmChoice.DisplayName);
        _llmChoice.SelectedIndexChanged += (_, _) => UpdateModelRows();
        _offline.DownloadChanged += UpdateModelRows;
        FormClosed += (_, _) => _offline.DownloadChanged -= UpdateModelRows;
        _onlineMode.CheckedChanged += (_, _) =>
        {
            UpdateMode();
            // Switching to online shows the key field: say right away whether the stored key works.
            if (_onlineMode.Checked && IsHandleCreated) CheckVisibleKeys();
        };

        // ================= page: Info =================
        BuildInfoPage();

        // ================= frame: navigation, page host, buttons =================
        var nav = new Panel { Dock = DockStyle.Left, Width = 260, Padding = new Padding(8, 12, 8, 8) };
        var logo = new PictureBox
        {
            Image = TrayIcons.App.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(20, 18), Size = new Size(20, 20),
        };
        var appTitle = new ThemedLabel
        {
            Text = "WhisperShortcut", AutoSize = false, Location = new Point(48, 16), Size = new Size(200, 24),
            Font = Theme.AppTitle, TextAlign = ContentAlignment.MiddleLeft,
        };
        nav.Controls.Add(logo);
        nav.Controls.Add(appTitle);
        int y = 60;
        foreach (var (name, icon) in Pages)
        {
            var item = new NavItem(name, icon) { Location = new Point(8, y), Size = new Size(244, 38), TabStop = false };
            item.Picked += (_, _) => ShowPage(name);
            _nav.Add(item);
            nav.Controls.Add(item);
            y += 40;
        }

        _pageHost = new Panel { Dock = DockStyle.Fill };
        foreach (var page in _pages.Values) _pageHost.Controls.Add(page);

        var save = new FluentButton("Speichern", accent: true) { MinimumSize = new Size(120, 32), TabIndex = 0 };
        var cancel = _close = new FluentButton("Schließen") { MinimumSize = new Size(120, 32), TabIndex = 1 };
        // WHY Save keeps the window open: a window that vanished on Save looked like a crash to testers.
        save.Click += (_, _) => TrySave();
        // "Abbrechen" (with changes) means discard: only the title bar's X and Esc ask whether to save.
        cancel.Click += (_, _) => { _discard = true; Close(); };
        _dirty = new ThemedLabel { Text = "Nicht gespeicherte Änderungen", Tone = Tone.Secondary, AutoSize = true, Visible = false, Margin = new Padding(0, 8, 12, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true,
            Padding = new Padding(28, 12, 28, 16),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(_dirty);
        // No CancelButton: Esc closes through ProcessCmdKey, which asks about unsaved changes.
        AcceptButton = save; // Enter in the key field saves; the multi-line boxes keep their Enter

        var content = new Panel { Dock = DockStyle.Fill };
        // Docking runs in reverse order of adding: buttons take the bottom, the page the rest.
        content.Controls.Add(_pageHost);
        content.Controls.Add(buttons);
        Controls.Add(content);
        Controls.Add(nav);
        ResumeLayout(false);
        PerformLayout();

        // The wheel moves the page on over a prompt box at its end and never changes a closed drop-down.
        WheelToPage.Attach(this);

        // Hints wrap at the visible page width, never wider.
        _pageHost.ClientSizeChanged += (_, _) => FitHints();

        Load += (_, _) =>
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            Theme.Apply(this);
            FitHints();
            _snapshot = Snapshot();
            _loadedOffline = _settings.OfflineMode;
        };
        UpdateMode();
        UpdateModelRows();
        ShowPage(General);
        Shown += (_, _) =>
        {
            var emptyKey = new[] { _geminiKey, _openAiKey }.FirstOrDefault(k => k.Card.Visible && k.Value.Length == 0);
            (emptyKey != null && _onlineMode.Checked ? (Control)emptyKey.Box : save).Focus();
            if (_onlineMode.Checked) CheckVisibleKeys();
        };

        // Any edit: show the "not saved" hint next to the buttons.
        var dirtyTimer = new System.Windows.Forms.Timer { Interval = 400 };
        // WHY skipped while a combo is open: Win32 reports the hot-tracked item as the selection, so hovering would flicker the hint.
        var combos = new[] { _mic, _llmChoice, _dictateModel, _promptModel, _speechEngine, _textEngine, _dictateModelOpenAi, _promptModelOpenAi };
        dirtyTimer.Tick += (_, _) =>
        {
            if (combos.Any(c => c.DroppedDown)) return;
            UpdateSaveState();
        };
        dirtyTimer.Start();
        FormClosing += OnClosing;
        Activated += (_, _) => { if (!IsDisposed && !_discard) SyncOfflineFromTray(); };
        // A running mic test keeps the microphone (and the Windows mic indicator) busy in the background.
        Deactivate += (_, _) => { if (_probe != null) StopMicTest(); };
        // Light/dark switched in Windows while the window is open: follow it.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            dirtyTimer.Dispose();
            _geminiKey.Dispose();
            _openAiKey.Dispose();
            StopMicTest();
            _meterTimer.Dispose();
            _sampleMenu.Dispose();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (!Theme.Refresh()) return;
            Core.Log.Info($"Settings theme {(Theme.Dark ? "dark" : "light")}");
            Theme.Apply(this);
        });
    }

    /// <summary>Opens one of the pages (<see cref="General"/>, <see cref="DictatePage"/>, …).</summary>
    public void ShowPage(string name)
    {
        if (!_pages.TryGetValue(name, out var page)) return;
        Core.Log.Info($"Settings page {name}");
        foreach (var p in _pages.Values) p.Visible = p == page;
        foreach (var n in _nav) n.Selected = n.Text == name;
        if (name != General) StopMicTest();
        FitHints();
    }

    // ---------------- pages ----------------

    /// <summary>A scrolling page: title, optional intro, then controls stacked top to bottom.</summary>
    private TableLayoutPanel NewPage(string title, string? intro)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(36, 24, 36, 24), ColumnCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Rows are added as controls come in; AutoSize rows keep the stack tight.
        root.ControlAdded += (_, _) => root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(PageTitle(title));
        if (intro != null) root.Controls.Add(Wrap(intro, 0));
        scroll.Controls.Add(root);
        _pages[title] = scroll;
        return root;
    }

    /// <summary>A page whose editor fills the height (glossary): no scroll, the text box grows.</summary>
    private void NewFillPage(string title, Control top, Control hint, Control editor, Control footer)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(36, 24, 36, 20), Visible = false };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (c, fill) in new[] { (PageTitle(title), false), (top, false), (hint, false), (editor, true), (footer, false) })
        {
            root.Controls.Add(c);
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        }
        _pages[title] = root;
    }

    private void BuildInfoPage()
    {
        // Order as in the macOS app's About tab: version and updates, welcome tour, data and reset, support.
        var info = NewPage(InfoPage, null);
        var head = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(RowPadding, 14, RowPadding, 14), Margin = new Padding(0) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.Controls.Add(new PictureBox { Image = new Icon(TrayIcons.App, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 16, 0) }, 0, 0);
        var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        names.Controls.Add(new ThemedLabel { Text = "WhisperShortcut für Windows", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0) });
        names.Controls.Add(new ThemedLabel
        {
            Text = $"Version {AppVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})",
            Tone = Tone.Secondary, AutoSize = true, Margin = new Padding(0, 2, 0, 0),
        });
        head.Controls.Add(names, 1, 0);
        var headCard = new Card();
        headCard.AddRow(head);
        _updateButton = new FluentButton("Suchen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Nach Updates suchen" };
        _updateButton.Click += (_, _) =>
        {
            if (_updates.Available != null) _updates.ShowDialog();
            else _ = CheckUpdateAsync();
        };
        headCard.AddRow(SettingRow("Nach Updates suchen", "", _updateButton, ButtonWidth, out _updateStatus));
        _updateStatus.Text = "Fragt GitHub nach der neuesten Version. Installiert wird erst nach deinem Klick.";
        if (_updates.Available != null) ShowUpdateFound(_updates.Available);
        _autoUpdate = new ToggleSwitch { Checked = _settings.AutoUpdateCheck, AccessibleName = "Automatisch nach Updates suchen" };
        // The practice runs in offline mode, which promises that nothing leaves the PC: say plainly what this one request sends.
        headCard.AddRow(SettingRow("Automatisch nach Updates suchen", "Kurz nach dem Start und einmal am Tag. Eine neue Version meldet sich "
            + "mit einem Hinweis; installiert wird erst, wenn du auf „Jetzt installieren“ klickst. Auch im Offline-Modus: Die App fragt "
            + "dabei nur github.com nach der neuesten Versionsnummer, ohne Aufnahmen, Texte oder Einstellungen.", _autoUpdate, ToggleWidth, out _));
        info.Controls.Add(headCard);

        info.Controls.Add(Section("Einführung"));
        var tour = new FluentButton("Zeigen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Einführung zeigen" };
        tour.Click += (_, _) =>
        {
            _welcome.Visible = true;
            ShowPage(General);
            ((ScrollableControl)_pages[General]).AutoScrollPosition = Point.Empty;
        };
        var tourCard = new Card();
        tourCard.AddRow(SettingRow("Einführung noch einmal zeigen", "Die Schritte vom ersten Start.", tour, ButtonWidth, out _));
        info.Controls.Add(tourCard);

        info.Controls.Add(Section("Daten und Zurücksetzen"));
        // Two folders on disk: %APPDATA% (settings, glossary, key) and %LOCALAPPDATA% (log and the Offline models).
        // Each row names what is really inside, so "Ordner öffnen" never surprises.
        var dataFolder = new FluentButton("Ordner öffnen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Ordner mit Einstellungen öffnen" };
        dataFolder.Click += (_, _) => OpenFolder(Core.AppData.SettingsFolder);
        var logFile = new FluentButton("Protokoll") { MinimumSize = new Size(96, 32), AccessibleName = "Protokolldatei öffnen" };
        logFile.Click += (_, _) =>
        {
            if (File.Exists(Core.Log.DefaultPath)) OpenUrl(Core.Log.DefaultPath);
            else OpenFolder(Core.AppData.LocalFolder);
        };
        var logFolder = new FluentButton("Ordner öffnen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Ordner mit Modellen und Protokoll öffnen" };
        logFolder.Click += (_, _) => OpenFolder(Core.AppData.LocalFolder);
        var logButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        logButtons.Controls.Add(logFile);
        logButtons.Controls.Add(logFolder);
        var reset = new FluentButton("Zurücksetzen …") { MinimumSize = new Size(ButtonWidth, 32), Danger = true, AccessibleName = "App zurücksetzen" };
        reset.Click += (_, _) => ResetApp();
        var dataCard = new Card();
        dataCard.AddRow(SettingRow("Einstellungen und Glossar", "Einstellungen, Glossar, eigene Anweisungen und der Schlüssel (verschlüsselt).",
            dataFolder, ButtonWidth, out _));
        dataCard.AddRow(SettingRow("Offline-Modelle und Protokoll", "Die geladenen Modelle (Unterordner Offline) und das Protokoll. "
            + "Das Protokoll hilft bei Fehlermeldungen und enthält keine diktierten Texte und keinen Schlüssel.", logButtons, ButtonWidth + 104, out _));
        dataCard.AddRow(SettingRow("App zurücksetzen", "Löscht alles, auch den API-Schlüssel, das Glossar und die Offline-Modelle. "
            + "Danach beendet sich WhisperShortcut und startet beim nächsten Mal wie neu installiert.", reset, ButtonWidth, out _));
        info.Controls.Add(dataCard);

        info.Controls.Add(Section("Hilfe und Entwickler"));
        var links = new Card();
        links.AddRow(SettingRow("Entwickelt von Magnus Goedde", "github.com/mgsgde/whisper-shortcut-windows",
            LinkButton("GitHub", "https://github.com/mgsgde/whisper-shortcut-windows"), ButtonWidth, out _));
        var report = new FluentButton("Melden …") { MinimumSize = new Size(96, 32), AccessibleName = "Problem melden" };
        report.Click += (_, _) => ReportProblem();
        var reportLog = new FluentButton("Protokoll") { MinimumSize = new Size(96, 32), AccessibleName = "Protokollordner zum Anhängen öffnen" };
        reportLog.Click += (_, _) => OpenFolder(Core.AppData.LogFolder);
        var reportButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        reportButtons.Controls.Add(report);
        reportButtons.Controls.Add(reportLog);
        links.AddRow(SettingRow("Problem melden", "Öffnet ein GitHub-Issue, schon ausgefüllt mit Version, Windows, Modellen und den "
            + "letzten Protokollzeilen, ohne diktierte Texte und ohne Schlüssel. Oben eintragen, was passiert ist, und vor dem "
            + "Absenden durchsehen. Klappt der Link nicht: die Protokolldatei aus dem Ordner anhängen.", reportButtons, 208, out _));
        info.Controls.Add(links);

        info.Controls.Add(Section("Lizenz"));
        info.Controls.Add(Wrap("Freie Software unter der GNU AGPL-3.0. Die Windows-Version ist ein Ableger von WhisperShortcut für macOS.", 0));
    }

    private static string AppVersion => Application.ProductVersion.Split('+')[0];

    /// <summary>Opens a prefilled GitHub issue; a body too long for the URL goes to the clipboard instead.</summary>
    private void ReportProblem()
    {
        var info = new Core.ProblemReportInfo(AppVersion, Environment.OSVersion.Version.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), _settings.OfflineMode,
            _settings.TranscriptionModel, _settings.PromptModel, _settings.OfflineLlmId);
        var body = Core.ProblemReport.Body(info, Core.ProblemReport.TailLog(Core.Log.DefaultPath));
        var (url, bodyInUrl) = Core.ProblemReport.IssueUrl($"Problem in Version {AppVersion}", body);
        if (!bodyInUrl)
        {
            try
            {
                Clipboard.SetText(body);
                MessageBox.Show(this, "Der Bericht ist zu lang für den Link und liegt jetzt in der Zwischenablage. Im Browser ins "
                    + "Textfeld klicken und Strg+V drücken.", "Problem melden", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                Core.Log.Error($"Copying the problem report failed: {e.GetType().Name}");
                Warn("Der Bericht konnte nicht in die Zwischenablage. Bitte die Protokolldatei aus dem Ordner an das Issue anhängen.");
                OpenFolder(Core.AppData.LogFolder);
            }
        }
        Core.Log.Info($"Problem report opened (body in URL: {bodyInUrl})");
        OpenUrl(url);
    }

    private async Task CheckUpdateAsync()
    {
        _updateButton.Enabled = false;
        _updateStatus.Text = "Wird gesucht …";
        _updateStatus.Tone = Tone.Secondary;
        try
        {
            var latest = await _updates.CheckAsync(manual: true);
            if (IsDisposed) return;
            if (latest != null) ShowUpdateFound(latest);
            else
            {
                _updateStatus.Text = $"✓ Version {AppVersion} ist die neueste.";
                _updateStatus.Tone = Tone.Good;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Core.Log.Error($"Update check failed: {e.GetType().Name}");
            if (IsDisposed) return;
            _updateStatus.Text = "Keine Verbindung zu GitHub. Später noch einmal versuchen.";
            _updateStatus.Tone = Tone.Caution;
        }
        if (!IsDisposed) _updateButton.Enabled = true;
    }

    private void ShowUpdateFound(Core.UpdateInfo update)
    {
        _updateStatus.Text = $"Version {update.Version} ist da. „Installieren“ zeigt, was neu ist.";
        _updateStatus.Tone = Tone.Accent;
        _updateButton.Text = "Installieren …";
        _updateButton.Accent = true;
    }

    private void ResetApp()
    {
        if (ChoiceDialog.Ask(this, "App zurücksetzen und beenden?",
            "Gelöscht werden alle Einstellungen, der API-Schlüssel, das Glossar, die Schnellanweisungen, die Vorlage und die "
            + "eigenen Anweisungen mit ihren früheren Fassungen, die geladenen Offline-Modelle und das Protokoll. Der Start mit Windows wird abgeschaltet.\n\n"
            + "Danach beendet sich WhisperShortcut. Beim nächsten Start ist es wie neu installiert. Das lässt sich nicht rückgängig machen.",
            new[] { "Zurücksetzen", "Abbrechen" }, danger: true) != 0) return;
        Core.Log.Info("Settings: reset app");
        StopMicTest();
        // The language model server and the speech model keep files open that the reset has to delete.
        _offline.Dispose();
        AutoStart.Disable();
        try { Core.AppData.Reset(); }
        catch (Exception e)
        {
            Warn("Nicht alles konnte gelöscht werden, vermutlich ist eine Datei noch geöffnet. WhisperShortcut beendet sich jetzt; "
                + "danach den App-Datenordner von Hand löschen.\n\n" + e.Message);
        }
        _discard = true;
        Application.Exit();
    }

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception e) { Core.Log.Error($"Creating folder failed: {e.GetType().Name}"); }
        OpenUrl(path);
    }

    private FluentButton LinkButton(string text, string url)
    {
        var button = new FluentButton(text) { MinimumSize = new Size(ButtonWidth, 32) };
        button.Click += (_, _) => OpenUrl(url);
        return button;
    }

    /// <summary>Card with one row: "Tastenkürzel", a description, the hotkey field on the right.</summary>
    private Card KeyCard(HotkeyBox box, string description)
    {
        var card = new Card();
        card.AddRow(SettingRow("Tastenkürzel", description, new FieldFrame(box) { Width = KeyWidth, Dock = DockStyle.None }, KeyWidth, out _));
        return card;
    }

    private static FluentCombo ModelChoice(IReadOnlyList<Core.GeminiModel> models, string selected)
    {
        var combo = new FluentCombo { Width = ComboWidth };
        foreach (var m in models) combo.Items.Add(new ModelItem(m));
        combo.SelectedItem = combo.Items.Cast<ModelItem>().FirstOrDefault(i => i.Model.Id == selected) ?? combo.Items[0];
        return combo;
    }

    /// <summary>Model card ("Gemini-Modell" / "OpenAI-Modell") with the chosen model's one-line description under the title.</summary>
    private Card ModelCard(FluentCombo combo, string title, string note)
    {
        combo.AccessibleName = title;
        var card = new Card();
        card.AddRow(SettingRow(title, "", combo, ComboWidth, out var description));
        void Describe() => description.Text = ((ModelItem)combo.SelectedItem!).Model.Description;
        combo.SelectedIndexChanged += (_, _) => Describe();
        Describe();
        var noteBlock = Block();
        noteBlock.Controls.Add(Wrap(note, 2 * RowPadding, small: true));
        card.AddRow(noteBlock);
        return card;
    }

    private sealed record ModelItem(Core.GeminiModel Model)
    {
        public override string ToString() => Model.Name;
    }

    private Card PromptCard(PromptEditor editor, HistoryLink history)
    {
        var block = Block();
        block.Controls.Add(new FieldFrame(editor.Box, multiline: true) { Height = 240 });
        var footer = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.State.Anchor = AnchorStyles.Left;
        history.Anchor = AnchorStyles.Right;
        history.Margin = new Padding(0, 4, 20, 0);
        editor.Reset.Anchor = AnchorStyles.Right;
        footer.Controls.Add(editor.State, 0, 0);
        footer.Controls.Add(history, 1, 0);
        footer.Controls.Add(editor.Reset, 2, 0);
        block.Controls.Add(footer);
        var card = new Card();
        card.AddRow(block);
        return card;
    }

    /// <summary>
    /// A system prompt field that shows the default text until the user changes it. Saved as "" while it
    /// equals the default, so a later improvement of the default reaches the user.
    /// </summary>
    private sealed class PromptEditor
    {
        private readonly string _default;
        public TextBox Box { get; }
        public ThemedLabel State { get; } = new() { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 4, 0, 0) };
        public LinkLabel Reset { get; } = new() { Text = "Standard wiederherstellen", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

        public PromptEditor(string defaultText, string stored, string name)
        {
            _default = defaultText;
            Box = new TextBox
            {
                Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, AccessibleName = name,
                Text = (stored.Length > 0 ? stored : defaultText).ReplaceLineEndings("\r\n"),
            };
            Box.TextChanged += (_, _) => Update();
            Reset.LinkClicked += (_, _) => Box.Text = _default.ReplaceLineEndings("\r\n");
            Update();
        }

        /// <summary>What to store: "" for the default.</summary>
        public string Value => Core.Settings.CustomOrEmpty(Box.Text, _default);

        private void Update()
        {
            bool custom = Value.Length > 0;
            State.Text = custom ? "Angepasst" : Box.Text.Trim().Length == 0 ? "Leer: es gilt der Standard" : "Standard";
            Reset.Enabled = custom || Box.Text.Trim().Length == 0;
        }
    }

    /// <summary>
    /// "Frühere Fassungen ▾" under a prompt field, shown only once that field has history. A pick only fills the
    /// field; Save keeps it, and the text it replaced goes into the history, so a restore can be undone the same way.
    /// </summary>
    private sealed class HistoryLink : LinkLabel
    {
        private readonly TextBox _box;
        private readonly Core.PromptHistory _history;
        private readonly Core.PromptField _field;
        private readonly ContextMenuStrip _menu = new() { ShowItemToolTips = true };

        public HistoryLink(TextBox box, Core.PromptHistory history, Core.PromptField field)
        {
            (_box, _history, _field) = (box, history, field);
            Text = "Frühere Fassungen ▾";
            AutoSize = true;
            AccessibleName = "Frühere Fassungen von " + box.AccessibleName;
            LinkClicked += (_, _) => { Theme.Menu(_menu); _menu.Show(this, new Point(0, Height)); };
            Reload();
        }

        /// <summary>Rebuilds the menu from the history; hides the link while there is none.</summary>
        public void Reload()
        {
            var versions = _history.Versions(_field);
            Visible = versions.Count > 0;
            _menu.Items.Clear();
            foreach (var v in versions)
            {
                var item = new ToolStripMenuItem(Label(v))
                {
                    ToolTipText = v.Text.Length > 600 ? v.Text[..600] + " …" : v.Text, AccessibleName = "Fassung bis " + When(v),
                };
                var text = v.Text;
                item.Click += (_, _) => _box.Text = text.ReplaceLineEndings("\r\n");
                _menu.Items.Add(item);
            }
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Einsetzen ändert nur das Feld. Erst „Speichern“ übernimmt es.") { Enabled = false });
        }

        /// <summary>"bis 28.10., 14:32 · first line …": until when the text was the saved one.</summary>
        private static string Label(Core.PromptVersion v)
        {
            var first = v.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return $"bis {When(v)}  ·  {Core.RecentTexts.Preview(first, 50)}";
        }

        private static string When(Core.PromptVersion v)
        {
            var at = v.ReplacedAt.ToLocalTime();
            return at.ToString(at.Year == DateTime.Now.Year ? "dd.MM., HH:mm" : "dd.MM.yyyy, HH:mm");
        }
    }

    /// <summary>Page title, 20 pt like the Windows 11 settings.</summary>
    private static Label PageTitle(string text) => new ThemedLabel
    {
        Text = text, AutoSize = true, Font = Theme.Title, Margin = new Padding(0, 0, 0, 10),
    };

    /// <summary>Group heading above a card.</summary>
    private static Label Section(string text, bool first = false) => new ThemedLabel
    {
        Text = text, AutoSize = true, Font = Theme.Section, Margin = new Padding(0, first ? 6 : 22, 0, 8),
    };

    /// <summary>Wrapping secondary text; <paramref name="inset"/> = how much narrower than the page (96 dpi).</summary>
    private Label Wrap(string text, int inset, bool small = false)
    {
        var label = new ThemedLabel
        {
            Text = text, AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 2, 0, 6),
            Font = small ? Theme.Small : Theme.Body,
        };
        Fit(label, inset);
        return label;
    }

    private void Fit(Label label, int inset)
    {
        label.MaximumSize = new Size(560, 0); // replaced by FitHints once the real width is known
        _wraps.Add((label, inset));
    }

    /// <summary>Free-form content inside a card (key field, microphone, prompt editor).</summary>
    private static TableLayoutPanel Block()
    {
        var block = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(RowPadding, 12, RowPadding, 12), Margin = new Padding(0),
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        block.ControlAdded += (_, _) => block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return block;
    }

    /// <summary>One card row: title and description on the left, the control on the right (Windows 11 settings row).</summary>
    private TableLayoutPanel SettingRow(string title, string? description, Control? right, int rightWidth, out ThemedLabel descriptionLabel)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(RowPadding, 12, RowPadding, 12), Margin = new Padding(0), MinimumSize = new Size(0, 64),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        var titleLabel = new ThemedLabel { Text = title, AutoSize = true, Margin = new Padding(0) };
        Fit(titleLabel, 2 * RowPadding + rightWidth + RowGap);
        text.Controls.Add(titleLabel);
        descriptionLabel = new ThemedLabel { Text = description ?? "", AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0), Visible = description != null };
        Fit(descriptionLabel, 2 * RowPadding + rightWidth + RowGap);
        text.Controls.Add(descriptionLabel);
        row.Controls.Add(text, 0, 0);
        if (right != null)
        {
            right.Anchor = AnchorStyles.Right;
            right.Margin = new Padding(RowGap, 0, 0, 0);
            row.Controls.Add(right, 1, 0);
        }
        return row;
    }

    /// <summary>An option (online/offline) with its explanation below, as one card row.</summary>
    private TableLayoutPanel OptionRow(OptionButton option, string description)
    {
        var block = Block();
        block.Controls.Add(option);
        var hint = Wrap(description, 2 * RowPadding + 32, small: true);
        hint.Margin = new Padding(32, 0, 0, 0);
        block.Controls.Add(hint);
        return block;
    }

    /// <summary>A fixed key like "Esc", right-aligned in a row.</summary>
    private static Label KeyCap(string keys) => new ThemedLabel { Text = keys, AutoSize = true, Font = Theme.Strong, Tone = Tone.Secondary };

    /// <summary>Field (or combo) filling the width, buttons at its right.</summary>
    private static TableLayoutPanel Row(Control fill, params Control[] right)
    {
        var row = new TableLayoutPanel { ColumnCount = 1 + right.Length, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fill.Dock = DockStyle.Fill;
        fill.Margin = new Padding(0, 0, 0, 0);
        row.Controls.Add(fill, 0, 0);
        for (int i = 0; i < right.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            right[i].Anchor = AnchorStyles.Left;
            right[i].Margin = new Padding(8, 0, 0, 0);
            row.Controls.Add(right[i], i + 1, 0);
        }
        return row;
    }

    private void ReplaceTemplate(string text)
    {
        if (_template.Text.Trim().Length > 0 && _template.Text.Trim() != text.Trim()
            && ChoiceDialog.Ask(this, "Vorlage ersetzen?", "Die jetzige Vorlage wird ersetzt.",
                new[] { "Ersetzen", "Abbrechen" }) != 0) return;
        _template.Text = text.ReplaceLineEndings("\r\n");
    }

    private void UpdateGlossaryCount()
    {
        int n = _glossary.Lines.Count(l => l.Trim().Length > 0);
        _glossaryCount.Text = n switch { 0 => "Noch keine Begriffe", 1 => "1 Begriff", _ => $"{n} Begriffe" };
    }

    // ---------------- API key ----------------

    private void CheckVisibleKeys()
    {
        foreach (var key in new[] { _geminiKey, _openAiKey })
            if (key.Card.Visible && key.Value.Length > 0) _ = key.CheckAsync();
    }

    /// <summary>One API key: the field, Anzeigen / Prüfen, and a check that runs while the key is pasted.</summary>
    private sealed class KeyField : IDisposable
    {
        public TextBox Box { get; }
        public ThemedLabel Status { get; } = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0), Text = "" };
        public Control Section { get; set; } = null!;
        public Card Card { get; set; } = null!;
        public string Value => Box.Text.Trim();

        private readonly Func<string, CancellationToken, Task<Core.KeyCheck>> _check;
        private readonly Func<Core.KeyCheck, (string Text, Tone Tone)> _message;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 700 };
        private CancellationTokenSource? _cts;

        public KeyField(string value, Func<string, CancellationToken, Task<Core.KeyCheck>> check,
            Func<Core.KeyCheck, (string Text, Tone Tone)> message)
        {
            _check = check;
            _message = message;
            Box = new TextBox { UseSystemPasswordChar = true, Text = value };
            // Pasting a key is the moment to know whether it works, not after Speichern.
            Box.TextChanged += (_, _) =>
            {
                SetStatus("", Tone.Secondary);
                _debounce.Stop();
                if (Value.Length >= 20) _debounce.Start();
            };
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = CheckAsync(); };
        }

        public async Task CheckAsync()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            string key = Value;
            if (key.Length == 0)
            {
                SetStatus("", Tone.Secondary);
                return;
            }
            SetStatus("Wird geprüft …", Tone.Secondary);
            Core.KeyCheck result;
            try { result = await _check(key, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Core.Log.Error($"Key check failed: {e.GetType().Name}");
                result = Core.KeyCheck.Other;
            }
            if (cts.IsCancellationRequested || Box.IsDisposed) return;
            var (text, tone) = _message(result);
            SetStatus(text, tone);
        }

        private void SetStatus(string text, Tone tone)
        {
            Status.Text = text;
            Status.Tone = tone;
            Status.Visible = text.Length > 0;
        }

        public void Dispose()
        {
            _debounce.Dispose();
            _cts?.Cancel();
        }
    }

    private void AddKeyCard(Control page, KeyField key, string title, string note, string linkText, string url)
    {
        key.Section = Section(title);
        page.Controls.Add(key.Section);
        key.Box.AccessibleName = title;
        var show = new FluentButton("Anzeigen") { MinimumSize = new Size(100, 32) };
        show.Click += (_, _) =>
        {
            key.Box.UseSystemPasswordChar = !key.Box.UseSystemPasswordChar;
            show.Text = key.Box.UseSystemPasswordChar ? "Anzeigen" : "Verbergen";
        };
        var check = new FluentButton("Prüfen") { MinimumSize = new Size(100, 32) };
        check.Click += (_, _) => _ = key.CheckAsync();
        var block = Block();
        block.Controls.Add(Row(new FieldFrame(key.Box), show, check));
        Fit(key.Status, 2 * RowPadding);
        block.Controls.Add(key.Status);
        block.Controls.Add(Wrap(note, 2 * RowPadding, small: true));
        var link = new LinkLabel { Text = linkText, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        link.LinkClicked += (_, _) => OpenUrl(url);
        block.Controls.Add(link);
        key.Card = new Card();
        key.Card.AddRow(block);
        page.Controls.Add(key.Card);
    }

    private sealed record EngineItem(Core.Engine Engine, string Label)
    {
        public override string ToString() => Label;
    }

    private static FluentCombo EngineChoice(Core.Engine selected, string name, string localLabel)
    {
        var combo = new FluentCombo { Width = ComboWidth, AccessibleName = name };
        combo.Items.Add(new EngineItem(Core.Engine.Gemini, "Gemini (Google)"));
        combo.Items.Add(new EngineItem(Core.Engine.OpenAI, "OpenAI"));
        combo.Items.Add(new EngineItem(Core.Engine.Local, localLabel));
        combo.SelectedItem = combo.Items.Cast<EngineItem>().First(i => i.Engine == selected);
        return combo;
    }

    private static Core.Engine SelectedEngine(FluentCombo combo) => ((EngineItem)combo.SelectedItem!).Engine;

    // ---------------- microphone ----------------

    private sealed record MicItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _refillingMics;

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

    private void RefreshMicrophones()
    {
        var selected = _mic.SelectedItem is MicItem current ? current.Value : "";
        var wanted = new List<MicItem> { new("", "Windows-Standard") };
        wanted.AddRange(Microphones.Names().Select(n => new MicItem(n, n)));
        if (!wanted.Any(i => i.Value == selected))
            wanted.Add(new MicItem(selected, $"{selected} (nicht angeschlossen, solange gilt Windows-Standard)"));
        if (wanted.SequenceEqual(_mic.Items.Cast<MicItem>())) return; // same devices: leave everything untouched
        _refillingMics = true;
        try
        {
            _mic.Items.Clear();
            foreach (var item in wanted) _mic.Items.Add(item);
            _mic.SelectedItem = wanted.First(i => i.Value == selected);
        }
        finally { _refillingMics = false; }
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
        _micMeter.Value = 0;
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

    /// <summary>Online shows the key, offline a summary of the models. Hidden, not greyed out: one way of working at a time.</summary>
    private void UpdateMode()
    {
        SuspendLayout();
        bool online = _onlineMode.Checked;
        var engines = online ? new[] { SelectedEngine(_speechEngine), SelectedEngine(_textEngine) } : new[] { Core.Engine.Local };
        foreach (var c in _engineOnly) c.Visible = online;
        _geminiKey.Section.Visible = _geminiKey.Card.Visible = engines.Contains(Core.Engine.Gemini);
        _openAiKey.Section.Visible = _openAiKey.Card.Visible = engines.Contains(Core.Engine.OpenAI);
        foreach (var c in _offlineOnly) c.Visible = engines.Contains(Core.Engine.Local);
        bool openAiSpeech = online && SelectedEngine(_speechEngine) == Core.Engine.OpenAI;
        bool openAiText = online && SelectedEngine(_textEngine) == Core.Engine.OpenAI;
        (_dictateGeminiCard.Visible, _dictateOpenAiCard.Visible) = (!openAiSpeech, openAiSpeech);
        (_promptGeminiCard.Visible, _promptOpenAiCard.Visible) = (!openAiText, openAiText);
        _offlineState.Text = _offlineMode.Checked
            ? "Der Offline-Modus ist an: Aufnahmen und Texte bleiben auf diesem PC. Beides muss einmal geladen sein."
            : SelectedEngine(_speechEngine) == Core.Engine.Local && SelectedEngine(_textEngine) != Core.Engine.Local
                ? "Die Spracherkennung läuft auf diesem PC, Texte gehen online. Nur die Spracherkennung muss geladen sein."
            : SelectedEngine(_textEngine) == Core.Engine.Local && SelectedEngine(_speechEngine) != Core.Engine.Local
                ? "Texte werden auf diesem PC bearbeitet, die Spracherkennung läuft online. Dafür muss das Sprachmodell geladen sein."
            : "Der Offline-Modus ist aus (umschalten unter Allgemein → Arbeitsweise). Laden geht schon vorher.";
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
        if (ChoiceDialog.Ask(this, "Modell entfernen?", $"{name} wird von diesem PC gelöscht. Für den Offline-Modus muss es danach neu geladen werden.",
                new[] { "Entfernen", "Abbrechen" }, danger: true) != 0) return;
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
        bool speechReady = OfflineEngine.SpeechInstalled, llmReady = OfflineEngine.LlmInstalled(llm);
        UpdateRow(_speechRow, "speech", speechReady, ComponentSize(Core.OfflineCatalog.Parakeet), download);
        UpdateRow(_llmRow, "llm", llmReady, llmParts.Sum(ComponentSize), download);
        _llmChoice.Enabled = download?.Id != "llm";
        (_offlineSummary.Text, _offlineSummary.Tone) =
            download != null ? ($"Wird geladen … {(int)Math.Round(download.Fraction * 100)} %", Tone.Secondary)
            : speechReady && llmReady ? ("✓ Beide geladen", Tone.Good)
            : speechReady ? ("Spracherkennung geladen. Für „Text bearbeiten“ fehlt noch das Sprachmodell.", Tone.Caution)
            : ("Noch nicht geladen. Ohne die Spracherkennung geht offline nichts.", Tone.Caution);
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
            row.Progress.Value = Math.Clamp(percent, 0, 100) / 100f;
            row.SetStatus($"Wird geladen … {percent} % von {FormatBytes(size)}", Tone.Secondary);
        }
        else if (installed)
        {
            row.SetStatus($"✓ Geladen ({FormatBytes(size)} auf diesem PC)", Tone.Good);
        }
        else if (error != null)
        {
            row.SetStatus(error, Tone.Bad);
        }
        else
        {
            row.SetStatus($"Noch nicht geladen, ca. {FormatBytes(size)}", Tone.Secondary);
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

    /// <summary>One model as a card row: name, status line, Laden/Abbrechen button, progress bar, "Entfernen" link.</summary>
    private sealed class ModelRow : TableLayoutPanel
    {
        private readonly ThemedLabel _status;
        public FluentButton Action { get; }
        public LinkLabel Remove { get; }
        public Bar Progress { get; }

        public ModelRow(string title, string? subtitle, SettingsForm form)
        {
            ColumnCount = 2;
            Dock = DockStyle.Top;
            AutoSize = true;
            Padding = new Padding(RowPadding, 12, RowPadding, 12);
            Margin = new Padding(0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            text.Controls.Add(new ThemedLabel { Text = title, AutoSize = true, Font = Theme.Strong, Margin = new Padding(0) });
            if (subtitle != null)
            {
                var sub = new ThemedLabel { Text = subtitle, AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) };
                form.Fit(sub, 2 * RowPadding + ButtonWidth + RowGap);
                text.Controls.Add(sub);
            }
            _status = new ThemedLabel { AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 4, 0, 0) };
            form.Fit(_status, 2 * RowPadding + ButtonWidth + RowGap);
            text.Controls.Add(_status);

            Action = new FluentButton("Laden") { MinimumSize = new Size(ButtonWidth, 32), Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(RowGap, 2, 0, 0) };
            Remove = new LinkLabel { Text = "Entfernen", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(RowGap, 8, 0, 0) };
            var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right | AnchorStyles.Top };
            actions.Controls.Add(Action);
            actions.Controls.Add(Remove);

            Progress = new Bar(level: false) { Margin = new Padding(0, 10, 0, 0), Visible = false };

            Controls.Add(text, 0, 0);
            Controls.Add(actions, 1, 0);
            Controls.Add(Progress, 0, 1);
            SetColumnSpan(Progress, 2);
        }

        public void SetStatus(string text, Tone tone)
        {
            _status.Text = text;
            _status.Tone = tone;
        }
    }

    private void FitHints()
    {
        // Page padding (72 at 96 dpi) and a possible scroll bar.
        int width = _pageHost.ClientSize.Width - LogicalToDeviceUnits(72) - SystemInformation.VerticalScrollBarWidth;
        foreach (var (label, inset) in _wraps)
            label.MaximumSize = new Size(Math.Max(width - LogicalToDeviceUnits(inset), LogicalToDeviceUnits(200)), 0);
    }

    // ---------------- save / close ----------------

    /// <summary>Everything the user can change, as one string: equal = nothing to save. The mode comes first (see SyncOfflineFromTray).</summary>
    private string Snapshot() => string.Join("\u0001", new[]
    {
        _onlineMode.Checked.ToString(), SelectedLlmChoice.Id, _geminiKey.Value, _openAiKey.Value, SelectedMicrophone,
        SelectedEngine(_speechEngine).ToString(), SelectedEngine(_textEngine).ToString(),
        SelectedModel(_dictateModelOpenAi), SelectedModel(_promptModelOpenAi),
        _dictateKey.Value, _promptKey.Value, _glossaryKey.Value, _autoStart.Checked.ToString(),
        _glossary.Text, _template.Text, SelectedModel(_dictateModel), SelectedModel(_promptModel),
        _dictatePrompt.Value, _promptPrompt.Value, _autoPaste.Checked.ToString(), _restoreClipboard.Checked.ToString(), _sounds.Checked.ToString(),
        _autoUpdate.Checked.ToString(),
    }.Concat(_quick.Select(q => q.Text)));

    private static string SelectedModel(FluentCombo combo) => ((ModelItem)combo.SelectedItem!).Model.Id;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Esc out of habit must not throw away typed glossary terms: close the way the X does.
        // Hotkey fields record Esc themselves, an open dropdown closes itself.
        if (keyData == Keys.Escape && ActiveControl is not HotkeyBox && ActiveControl is not ComboBox { DroppedDown: true })
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// The tray menu can switch online/offline while this window is open. Take that over (unless the
    /// user changed the mode here too), so Save does not quietly switch back.
    /// </summary>
    private void SyncOfflineFromTray()
    {
        if (_snapshot.Length == 0 || _settings.OfflineMode == _loadedOffline) return;
        bool userChangedMode = _offlineMode.Checked != _loadedOffline;
        _loadedOffline = _settings.OfflineMode;
        if (userChangedMode) return;
        (_loadedOffline ? _offlineMode : _onlineMode).Checked = true;
        _snapshot = _onlineMode.Checked + _snapshot[_snapshot.IndexOf('\u0001')..];
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_discard || e.CloseReason != CloseReason.UserClosing || Snapshot() == _snapshot) return;
        switch (ChoiceDialog.Ask(this, "Änderungen speichern?", "Die Änderungen in den Einstellungen sind noch nicht gespeichert.",
                    new[] { "Speichern", "Verwerfen", "Zurück" }))
        {
            case 0: if (!TrySave()) e.Cancel = true; break;
            case 1: _discard = true; break;
            default: e.Cancel = true; break;
        }
    }

    /// <summary>Hint next to the buttons and the close button's word: unsaved changes, just saved, or nothing.</summary>
    private void UpdateSaveState()
    {
        bool dirty = _snapshot.Length > 0 && Snapshot() != _snapshot;
        _close.Text = dirty ? "Abbrechen" : "Schließen";
        if (dirty)
            (_dirty.Text, _dirty.Tone, _dirty.Visible) = ("Nicht gespeicherte Änderungen", Tone.Secondary, true);
        else
            (_dirty.Text, _dirty.Tone, _dirty.Visible) = ("✓ Gespeichert", Tone.Good, Environment.TickCount64 < _savedHintUntil);
    }

    private bool TrySave()
    {
        var keys = new[] { (Box: _dictateKey, Page: DictatePage), (Box: _promptKey, Page: PromptPage), (Box: _glossaryKey, Page: GlossaryPage) };
        var parsed = new Hotkey[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (!Hotkey.TryParse(keys[i].Box.Value, out parsed[i]) || !IsSafe(parsed[i]))
            {
                ShowPage(keys[i].Page);
                Warn("Bitte für jede Funktion ein Tastenkürzel mit zwei Zusatztasten festlegen, z. B. Strg+Umschalt+D. Strg+Alt allein geht nicht, das ist auf deutschen Tastaturen AltGr.");
                return false;
            }
        }
        for (int i = 1; i < keys.Length; i++)
        {
            if (parsed.Take(i).Contains(parsed[i]))
            {
                ShowPage(keys[i].Page);
                Warn("Die Tastenkürzel müssen verschieden sein. Diktieren, Text bearbeiten und Glossar brauchen je ein eigenes.");
                return false;
            }
        }

        var before = (_settings.OfflineMode, _settings.OfflineLlmId, _settings.ApiKey, _settings.Microphone,
            _settings.DictateHotkey, _settings.PromptHotkey, _settings.GlossaryHotkey, _settings.Glossary,
            _settings.QuickActions, _settings.DictatePromptTemplate, _settings.TranscriptionModel, _settings.PromptModel,
            _settings.DictateSystemPrompt, _settings.PromptModeSystemPrompt, _settings.AutoPaste, _settings.RestoreClipboard, _settings.Sounds,
            _settings.OpenAiKey, _settings.SpeechEngine, _settings.TextEngine, _settings.OpenAiTranscriptionModel, _settings.OpenAiPromptModel,
            _settings.AutoUpdateCheck);
        _settings.OfflineMode = _offlineMode.Checked;
        _settings.OfflineLlmId = SelectedLlmChoice.Id;
        _settings.ApiKey = _geminiKey.Value;
        _settings.OpenAiKey = _openAiKey.Value;
        _settings.SpeechEngine = SelectedEngine(_speechEngine);
        _settings.TextEngine = SelectedEngine(_textEngine);
        _settings.OpenAiTranscriptionModel = SelectedModel(_dictateModelOpenAi);
        _settings.OpenAiPromptModel = SelectedModel(_promptModelOpenAi);
        _settings.Microphone = SelectedMicrophone;
        _settings.DictateHotkey = parsed[0].ToString();
        _settings.PromptHotkey = parsed[1].ToString();
        _settings.GlossaryHotkey = parsed[2].ToString();
        _settings.Glossary = _glossary.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        _settings.QuickActions = Core.QuickActionList.Clean(_quick.Select(q => q.Text));
        _settings.DictatePromptTemplate = _template.Text.Trim().Length == 0 ? "" : _template.Text.ReplaceLineEndings("\n").Trim();
        _settings.TranscriptionModel = SelectedModel(_dictateModel);
        _settings.PromptModel = SelectedModel(_promptModel);
        _settings.DictateSystemPrompt = _dictatePrompt.Value;
        _settings.PromptModeSystemPrompt = _promptPrompt.Value;
        _settings.AutoPaste = _autoPaste.Checked;
        _settings.RestoreClipboard = _restoreClipboard.Checked;
        _settings.Sounds = _sounds.Checked;
        _settings.AutoUpdateCheck = _autoUpdate.Checked;
        try
        {
            _settings.Save();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Saving settings failed: {e.GetType().Name}");
            (_settings.OfflineMode, _settings.OfflineLlmId, _settings.ApiKey, _settings.Microphone,
                _settings.DictateHotkey, _settings.PromptHotkey, _settings.GlossaryHotkey, _settings.Glossary,
                _settings.QuickActions, _settings.DictatePromptTemplate, _settings.TranscriptionModel, _settings.PromptModel,
                _settings.DictateSystemPrompt, _settings.PromptModeSystemPrompt, _settings.AutoPaste, _settings.RestoreClipboard, _settings.Sounds,
            _settings.OpenAiKey, _settings.SpeechEngine, _settings.TextEngine, _settings.OpenAiTranscriptionModel, _settings.OpenAiPromptModel,
            _settings.AutoUpdateCheck) = before;
            Warn("Die Einstellungen konnten nicht gespeichert werden: " + e.Message);
            return false;
        }
        if (_autoStart.Checked != AutoStart.IsEnabled)
        {
            if (!(_autoStart.Checked ? AutoStart.Enable() : AutoStart.Disable()))
                Warn("Der automatische Start konnte nicht geändert werden. Details stehen im Protokoll.");
        }
        Core.Log.Info($"Settings saved (autostart {AutoStart.IsEnabled}, offline {_settings.OfflineMode}, llm {_settings.OfflineLlmId}, key set: {_settings.ApiKey.Length > 0}, mic {(_settings.Microphone.Length > 0 ? _settings.Microphone : "default")}, glossary {_settings.Glossary.Count}, quick actions {_settings.QuickActions.Count}, template {_settings.DictatePromptTemplate.Length} chars, models {_settings.TranscriptionModel}/{_settings.PromptModel}, own prompts {_settings.DictateSystemPrompt.Length}/{_settings.PromptModeSystemPrompt.Length} chars, auto-paste {_settings.AutoPaste}, restore clipboard {_settings.RestoreClipboard}, sounds {_settings.Sounds}, update check {_settings.AutoUpdateCheck}, engines {_settings.SpeechEngine}/{_settings.TextEngine}, openai key set: {_settings.OpenAiKey.Length > 0}, openai models {_settings.OpenAiTranscriptionModel}/{_settings.OpenAiPromptModel})");
        RecordPromptHistory(before.DictateSystemPrompt, before.PromptModeSystemPrompt, before.DictatePromptTemplate);
        _snapshot = Snapshot();
        _loadedOffline = _settings.OfflineMode;
        _savedHintUntil = Environment.TickCount64 + 3000;
        UpdateSaveState();
        Saved?.Invoke();
        return true;
    }

    /// <summary>After a save: the prompt texts that were replaced go into "Frühere Fassungen".</summary>
    private void RecordPromptHistory(string dictate, string promptMode, string template)
    {
        var now = DateTime.UtcNow;
        bool changed = _history.Record(Core.PromptField.Dictate, dictate, _settings.DictateSystemPrompt, now)
            | _history.Record(Core.PromptField.PromptMode, promptMode, _settings.PromptModeSystemPrompt, now)
            | _history.Record(Core.PromptField.Template, template, _settings.DictatePromptTemplate, now);
        if (!changed) return;
        try { _history.Save(); }
        catch (Exception e) { Core.Log.Error($"Saving prompt history failed: {e.GetType().Name}"); }
        _dictateHistory.Reload();
        _promptHistory.Reload();
        _templateHistory.Reload();
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
    /// that key away from every program. Ctrl+Alt without Shift is AltGr on German keyboards
    /// (Ctrl+Alt+Q would swallow every @).
    /// </summary>
    private static bool IsSafe(Hotkey hotkey)
    {
        uint m = hotkey.Modifiers;
        int count = ((m & MOD_CONTROL) != 0 ? 1 : 0) + ((m & MOD_ALT) != 0 ? 1 : 0) + ((m & MOD_SHIFT) != 0 ? 1 : 0);
        if ((m & (MOD_CONTROL | MOD_ALT)) == (MOD_CONTROL | MOD_ALT) && (m & MOD_SHIFT) == 0) return false;
        return count >= 2 && (m & (MOD_CONTROL | MOD_ALT)) != 0;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);

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
            ShortcutsEnabled = false;
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
                // Form-wide: the parent is the small field frame, where Tab would go nowhere.
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
