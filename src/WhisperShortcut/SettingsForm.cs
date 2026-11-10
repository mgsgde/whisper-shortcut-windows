using System.Diagnostics;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// Settings window in the style of the Windows 11 settings, pages and order as in the macOS app (SettingsTab):
/// Allgemein, Konten, Diktieren, Dictate Prompt, Vorlagen, Bildschirmfoto, Claude & KI-Apps, Datenschutz & Berechtigungen, Über; the page on the right as
/// rounded cards; every edit applies by itself (no Speichern, like the Mac). Light or dark follows Windows. German UI.
/// </summary>
internal sealed partial class SettingsForm : FastShowForm
{
    private readonly Core.Settings _settings;
    private readonly OfflineEngine _offline;
    private readonly Core.GeminiClient _gemini;
    private readonly Core.OpenAiClient _openAi;
    private readonly OptionButton _onlineMode;
    private readonly OptionButton _offlineMode;
    private readonly ModelRow _speechRow;
    private readonly ModelRow _llmRow;
    private readonly FluentCombo _llmChoice;
    private readonly ThemedLabel _offlineState;
    private readonly KeyField _geminiKey;
    private readonly KeyField _openAiKey;
    private readonly FluentCombo _speechEngine;
    private readonly FluentCombo _textEngine;
    private readonly FluentCombo _dictateModelOpenAi;
    private readonly FluentCombo _promptModelOpenAi;
    private readonly Control[] _engineOnly;
    private readonly Card _dictateGeminiCard, _dictateOpenAiCard, _promptGeminiCard, _promptOpenAiCard;
    private readonly FluentCombo _punctuation;
    /// <summary>Diktieren → Modus (Mac Dictate → Mode): what the Diktieren shortcut and right Ctrl start.</summary>
    private readonly FluentCombo _dictateMode;
    private readonly ThemedLabel _dictateModeText;
    /// <summary>The orange note under Modus when Live is chosen but cannot start, with its one-click fix.</summary>
    private readonly TableLayoutPanel _liveWarning;
    private readonly Label _liveWarningText;
    private readonly FluentButton _useParakeet;
    // Diktieren → Sprache (OpenAI only) and Erweitert → Genauigkeit (Gemini only), like the Mac.
    private readonly FluentCombo _language, _temperature, _thinking;
    /// <summary>Diktieren → Satzzeichen: only offline speech recognition writes spoken marks out as words.</summary>
    private readonly Control[] _punctuationOnly;
    private readonly FluentCombo _mic;
    private readonly FluentButton _micTest;
    private readonly Bar _micMeter;
    private readonly HotkeyBox _dictateKey;
    private readonly HotkeyBox _promptKey;
    private readonly HotkeyBox _glossaryKey;
    private readonly HotkeyBox _settingsKey;
    private readonly ToggleSwitch _rightCtrl;
    private readonly FluentCombo _dictateModel;
    private readonly FluentCombo _promptModel;
    private readonly PromptEditor _dictatePrompt;
    private readonly PromptEditor _promptPrompt;
    private readonly ToggleSwitch _autoStart;
    private readonly ToggleSwitch _autoPaste;
    private readonly FluentCombo _afterPaste;
    private readonly ToggleSwitch _sounds;
    private ThemedLabel _updateStatus = null!;
    private FluentButton _updateButton = null!;
    private readonly UpdateService _updates;
    private ToggleSwitch _autoUpdate = null!;
    private readonly TextBox _glossary;
    private readonly ThemedLabel _glossaryCount;
    private readonly TextBox _template;
    private readonly TextBox[] _quick = new TextBox[Core.QuickActionList.Max];
    // Vorlagen page: a working copy of the templates, written to the settings only on Save.
    private readonly List<Core.DictationTemplate> _templates;
    private readonly HotkeyBox _templateKey;
    private readonly ToggleSwitch _sendScreenshots;
    private readonly HotkeyBox _screenshotKey;
    private readonly HotkeyBox _liveKey;
    private readonly Label _screenshotOff;
    private readonly FluentCombo _templateChoice;
    private readonly FluentButton _templateRemove;
    private readonly Card _templateEditor;
    private readonly Label _templateEmpty;
    private readonly TextBox _templateName;
    private readonly TextBox _templateInstructions;
    private readonly HotkeyBox _templateOwnKey;
    private readonly ThemedLabel _templateState;
    // Set while the editor fields are filled from a template, so their change events do not write back.
    private bool _loadingTemplate;
    private readonly ContextMenuStrip _sampleMenu;
    private readonly Core.PromptHistory _history = Core.PromptHistory.Load(Core.PromptHistory.DefaultPath);
    private readonly HistoryLink _dictateHistory, _promptHistory, _templateHistory;
    private readonly ThemedLabel _dirty;
    private readonly List<(Label Label, int Inset)> _wraps = new();
    private readonly List<Control> _building = new();
    private readonly List<NavItem> _nav = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Panel _pageHost;
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    // Applies edits once they held still (see the constructor); runs only while the window is visible.
    private readonly System.Windows.Forms.Timer _applyTimer = new() { Interval = 150 };

    /// <summary>For the UI tests: whether the window does background work (edit check, AI-app clock).</summary>
    internal bool TimersRunningForTests => _applyTimer.Enabled || _mcpClock.Enabled;
    private LevelProbe? _probe;
    private string _snapshot = "";
    private bool _loadedOffline;
    // "✓ Gespeichert" stays in the status line until then (Environment.TickCount64).
    private long _savedHintUntil;
    // Mac: every edit applies on its own. A changed state is written once it held still this long (keys: 500 ms).
    private const int ApplyDelayMs = 500;
    private string _pendingSnapshot = "";
    private long _pendingSince;
    // Why the shortcuts were not taken over (invalid or taken twice); the rest of the edits still apply.
    private string? _keyProblem;
    private string? _lastSaveError;
    // The prompt texts when the window opened: replaced versions go into „Frühere Fassungen“ once, on close.
    private (string Dictate, string PromptMode, string Template) _openedPrompts;

    /// <summary>Raised after the settings were written; the window stays open.</summary>
    public event Action? Saved;

    private bool _discard;

    public const string General = "Allgemein", AccountsPage = "Konten", DictatePage = "Diktieren", PromptPage = "Dictate Prompt",
        TemplatesPage = "Vorlagen", ScreenshotPage = "Bildschirmfoto", PrivacyPage = "Datenschutz & Berechtigungen", InfoPage = "Über";

    // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs for the navigation.
    // Grouped under section headers as on the Mac (#381, SettingsTab.section): the order is the Mac's
    // section order, and SettingsTab.allCases order inside each section.
    private static readonly (string Name, string Icon, string Section)[] Pages =
    {
        (General, "\uE713", "Allgemein"), (AccountsPage, "\uE8D7", "Allgemein"), (PrivacyPage, "\uEA18", "Allgemein"),
        (DictatePage, "\uE720", "Diktat"), (PromptPage, "\uE70F", "Diktat"), (TemplatesPage, "\uE8A5", "Diktat"), (ReadAloudPage, "\uE767", "Diktat"),
        (ChatPage, "\uE8BD", "Chat & Meetings"), (MeetingsPage, "\uE7C8", "Chat & Meetings"),
        (ScreenshotPage, "\uE722", "Mehr"), (AiAppsPage, "\uE8F2", "Mehr"), (InfoPage, "\uE946", "Mehr"),
    };

    // Inset (96 dpi) of a description inside a card row with a control of this width on the right.
    private const int RowPadding = 16, RowGap = 24;
    private const int KeyWidth = 200, ComboWidth = 300, ToggleWidth = 88, ButtonWidth = 120;

    public SettingsForm(Core.Settings settings, OfflineEngine offline, Core.GeminiClient gemini, Core.OpenAiClient openAi, UpdateService updates, McpService? mcp = null)
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
        AccessibleDescription = Core.UserCopy.SettingsAccessibleDescription;
        Font = Theme.Body;
        StartPosition = FormStartPosition.Manual; // centered in Load, after the size is known
        ClientSize = new Size(1100, 780); // Mac: 1100×800 preferred, fitted to the work area in Load
        MinimumSize = new Size(760, 520);
        MaximizeBox = false;
        ShowInTaskbar = true;
        Icon = TrayIcons.App;

        // Pages and their order as in the macOS settings (SettingsTab): every page exists before the controls go onto it,
        // so each block below adds to the page where the Mac shows it.
        var general = NewPage(General, null);
        var accounts = NewPage(AccountsPage, "Verbinde mindestens einen Anbieter. Diktieren und Dictate Prompt wählen dann ein Modell "
            + "der hier verbundenen Anbieter. Offline-Modelle brauchen nichts davon.");
        var dictate = NewPage(DictatePage, "Taste drücken, sprechen, noch einmal drücken: Der Text steht dort, wo der Cursor blinkt.");
        var prompt = NewPage(PromptPage, "Text markieren, Kürzel drücken und sagen, was damit passieren soll („freundlicher“, "
            + "„ins Englische“), noch einmal drücken. Ohne Markierung schreibt das Modell einen neuen Text nach der Vorlage.");
        var templatesPage = NewPage(TemplatesPage, "Direkt in ein festes Format diktieren, z. B. eine Behandlungsdokumentation mit "
            + "Anamnese, Befund und Therapie: Kürzel drücken, wie gewohnt sprechen, noch einmal drücken. Ist beim Drücken Text markiert, "
            + "bringt die Vorlage die Markierung in das Format.");
        var screenshotPage = NewPage(ScreenshotPage, null);
        var privacy = NewPage(PrivacyPage, "Ob etwas diesen PC verlassen darf und worauf WhisperShortcut hier zugreifen kann.");

        // ================= page: Allgemein =================
        // --- shortcuts: all in one list, like the Mac's General → Keyboard Shortcuts ---
        general.Controls.Add(Section("Tastenkürzel", first: true));
        general.Controls.Add(Wrap("Globale Tastenkürzel – sie funktionieren in jeder App. Einmal drücken zum Starten, noch einmal zum Beenden. "
            + "Ins Feld klicken und die neue Kombination drücken, z. B. Alt+1; Entf entfernt ein Kürzel. Solange dieses Fenster vorne "
            + "ist, sind die Kürzel aus.", 0));
        _dictateKey = new HotkeyBox(settings.DictateHotkey) { AccessibleName = "Tastenkürzel Diktieren" };
        _promptKey = new HotkeyBox(settings.PromptHotkey) { AccessibleName = "Tastenkürzel Dictate Prompt" };
        _screenshotKey = new HotkeyBox(settings.ActiveScreenshotHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Bildschirmfoto" };
        // Empty = off (Mac: Add to Glossary ships disabled); the stored key stays as the suggestion.
        _glossaryKey = new HotkeyBox(settings.ActiveGlossaryHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Ins Glossar" };
        // Live diktieren (macOS Dictate Live): off until the user picks a key (Mac: shortcut ships disabled).
        _liveKey = new HotkeyBox(settings.ActiveLiveDictateHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Live diktieren" };
        _settingsKey = new HotkeyBox(settings.ActiveOpenSettingsHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Einstellungen öffnen" };
        var keysCard = new Card();
        keysCard.AddRow(KeyRow("Diktieren", null, _dictateKey));
        keysCard.AddRow(KeyRow("Dictate Prompt", "Markierten Text per Sprache bearbeiten. Ein Druck während der Verarbeitung bricht ab.", _promptKey));
        keysCard.AddRow(KeyRow("Bildschirmfoto", "Bereich aufziehen; das Bild landet in der Zwischenablage.", _screenshotKey));
        keysCard.AddRow(ReadAloudKeyRow()); // SettingsForm.ReadAloud.cs
        keysCard.AddRow(KeyRow("Ins Glossar", "Das richtig geschriebene Wort irgendwo markieren und das Kürzel drücken.", _glossaryKey));
        keysCard.AddRow(KeyRow("Live diktieren", "Der Text erscheint schon beim Sprechen, nach jeder kurzen Pause. Nur mit der "
            + "Spracherkennung auf diesem PC (Parakeet).", _liveKey));
        keysCard.AddRow(ChatKeyRow()); // SettingsForm.Chat.cs; Mac order: after Live diktieren, before Einstellungen
        keysCard.AddRow(KeyRow("Einstellungen öffnen", "Öffnet dieses Fenster, ein zweiter Druck schließt es.", _settingsKey));
        // Mac: „Also dictate with 🌐 Fn“. Windows has no Fn key; the right Ctrl takes its place (same toggle rules).
        _rightCtrl = new ToggleSwitch { Checked = settings.RightCtrlDictation, AccessibleName = "Auch mit der rechten Strg-Taste diktieren" };
        keysCard.AddRow(SettingRow("Auch mit der rechten Strg-Taste diktieren", "Rechte Strg einmal drücken zum Starten, noch einmal zum "
            + "Stoppen. Zusammen mit einer anderen Taste bleibt sie die normale Strg-Taste.", _rightCtrl, ToggleWidth, out _));
        general.Controls.Add(keysCard);

        // --- start ---
        general.Controls.Add(Section("Start", subtitle: "Ob WhisperShortcut mit Windows startet."));
        _autoStart = new ToggleSwitch { Checked = AutoStart.IsEnabled, AccessibleName = "Mit Windows starten" };
        var startCard = new Card();
        startCard.AddRow(SettingRow("Mit Windows starten", "Dann ist WhisperShortcut nach jedem Neustart gleich unten rechts im Infobereich da.",
            _autoStart, ToggleWidth, out _));
        // Windows tucks new tray icons behind the arrow ^; "always show" is a switch on the taskbar page of Windows Settings.
        var pinIcon = new FluentButton("Symbol immer anzeigen …") { MinimumSize = new Size(ButtonWidth + 56, 32), AccessibleName = "Taskleisten-Einstellungen von Windows öffnen" };
        pinIcon.Click += (_, _) => OpenLocal("ms-settings:taskbar");
        startCard.AddRow(SettingRow("Symbol immer anzeigen", "Windows versteckt das Symbol oft hinter dem Pfeil ^. In den Taskleisten-Einstellungen "
            + "unter „Weitere Taskleistensymbole“ WhisperShortcut einschalten, dann bleibt es sichtbar.", pinIcon, ButtonWidth + 56, out _));
        general.Controls.Add(startCard);

        // --- updates (Mac #346: General → Updates, right after Startup; Über holds information only) ---
        AddUpdatesSection(general);

        // --- microphone (Windows only: choice and level test) ---
        general.Controls.Add(Section("Mikrofon", subtitle: "Welches Mikrofon aufnimmt. Gibt es nur unter Windows."));
        _mic = new FluentCombo { Dock = DockStyle.Fill, AccessibleName = Core.UserCopy.MicAccessibleName };
        FillMicrophones(settings.Microphone);
        _micTest = new FluentButton("Testen") { MinimumSize = new Size(100, 32), AccessibleName = Core.UserCopy.MicTestAccessibleName };
        var micBlock = Block();
        micBlock.Controls.Add(Row(_mic, _micTest));
        _micMeter = new Bar(level: true) { Margin = new Padding(0, 8, 0, 4), Visible = false };
        micBlock.Controls.Add(_micMeter);
        micBlock.Controls.Add(Wrap("„Windows-Standard“ folgt dem Gerät, das in Windows unter System → Sound als Eingabe gewählt ist. "
            + "Ein Headset ist in der Praxis deutlich besser als das eingebaute Mikrofon. " + Core.UserCopy.MicTestHint,
            2 * RowPadding, small: true));
        var micCard = new Card();
        micCard.AddRow(micBlock);
        general.Controls.Add(micCard);
        _micTest.Click += (_, _) => ToggleMicTest();
        _mic.SelectedIndexChanged += (_, _) => { if (!_refillingMics && _probe != null) { StopMicTest(); ToggleMicTest(); } };
        // A headset plugged in while the window is open shows up when the list is opened.
        _mic.DropDown += (_, _) => RefreshMicrophones();
        _meterTimer.Tick += (_, _) => _micMeter.Value = _probe?.Level ?? 0;

        // --- clipboard (Mac: Auto-paste, After auto-paste) ---
        general.Controls.Add(Section("Zwischenablage", subtitle: "Was passiert, wenn Diktieren oder Dictate Prompt fertig ist. Der Text liegt immer auch in der Zwischenablage."));
        _autoPaste = new ToggleSwitch { Checked = settings.AutoPaste, AccessibleName = "Automatisch einfügen" };
        _afterPaste = new FluentCombo { Width = ComboWidth, AccessibleName = "Zwischenablage nach dem Einfügen" };
        FillChoices(_afterPaste, Core.Choices.AfterPaste, settings.ClipboardAfterPaste);
        var pasteCard = new Card();
        pasteCard.AddRow(SettingRow("Automatisch einfügen", "Der Text erscheint dort, wo der Cursor steht. Aus: Er liegt nur in der "
            + "Zwischenablage und wird mit Strg+V selbst eingefügt.", _autoPaste, ToggleWidth, out _));
        // Mac "After auto-paste": the explanation line follows the chosen option.
        pasteCard.AddRow(SettingRow("Nach dem Einfügen", "", _afterPaste, ComboWidth, out var afterPasteText));
        void DescribeAfterPaste() => afterPasteText.Text = Core.Choices.AfterPaste.Explanation(SelectedAfterPaste);
        _afterPaste.SelectedIndexChanged += (_, _) => DescribeAfterPaste();
        DescribeAfterPaste();
        general.Controls.Add(pasteCard);
        // Without pasting there is nothing to settle afterwards: the text itself stays in the clipboard (Mac: disabled).
        _autoPaste.CheckedChanged += (_, _) => _afterPaste.Enabled = _autoPaste.Checked;
        _afterPaste.Enabled = _autoPaste.Checked;

        // --- Erweitert (Mac: General → Advanced at the end): sounds (Windows only, off like the Mac) and the recording keys ---
        _sounds = new ToggleSwitch { Checked = settings.Sounds, AccessibleName = "Signaltöne" };
        var soundCard = new Card();
        soundCard.AddRow(SettingRow("Signaltöne", "Ein kurzer Ton beim Start und Ende einer Aufnahme und bei Fehlern. "
            + "Aus: Nur das Symbol und die Anzeige am Bildschirm zeigen, was gerade passiert.", _sounds, ToggleWidth, out _));
        var fixedCard = new Card();
        fixedCard.AddRow(SettingRow("Aufnahme verwerfen oder Verarbeitung abbrechen", null, KeyCap("Esc"), ButtonWidth + 40, out _));
        fixedCard.AddRow(SettingRow("Schnellanweisung anwenden (nur bei Dictate Prompt)", null, KeyCap(Core.QuickActionList.KeyRange), ButtonWidth + 40, out _));
        AddAdvanced(general,
            Section("Töne", first: true), soundCard,
            Section("Während einer Aufnahme", subtitle: "Diese Tasten sind nur belegt, solange die Anzeige unten zu sehen ist. "
                + "Sonst gehören sie wie gewohnt den anderen Programmen."), fixedCard);

        // --- Daten & Zurücksetzen: last on the page, where the Mac (#346) puts destructive actions ---
        AddDataSection(general);

        // ================= page: Konten (Mac Accounts: one section per provider key) =================
        _geminiKey = new KeyField(settings.ApiKey ?? "", (k, ct) => _gemini.CheckKeyAsync(k, ct), GeminiKeyMessage, k => SaveCheckedKey(k, gemini: true));
        AddKeyCard(accounts, _geminiKey, "Google-API-Schlüssel", "Für Gemini: Diktieren und Dictate Prompt. Bitte einen Schlüssel aus einem "
            + "Google-Projekt mit Abrechnung verwenden: Im kostenlosen Kontingent darf Google die Aufnahmen zum Training nutzen.",
            "Schlüssel in Google AI Studio erstellen", "https://aistudio.google.com/apikey");
        _openAiKey = new KeyField(settings.OpenAiKey ?? "", (k, ct) => _openAi.CheckKeyAsync(k, ct), OpenAiKeyMessage, k => SaveCheckedKey(k, gemini: false));
        AddKeyCard(accounts, _openAiKey, "OpenAI-API-Schlüssel", "Für die Transkriptions- und Textmodelle von OpenAI. Ein API-Schlüssel mit "
            + "Guthaben auf platform.openai.com; ein ChatGPT-Abo zählt dafür nicht.",
            "Schlüssel bei OpenAI erstellen", "https://platform.openai.com/api-keys");

        // ================= page: Diktieren =================
        // --- Modus (Mac SpeechToTextSettingsTab.dictateModeSection): above the model, because Live depends on it ---
        dictate.Controls.Add(Section("Modus", first: true,
            subtitle: "Was das Diktieren-Kürzel und die rechte Strg-Taste tun. Das Kürzel für Live diktieren startet immer Live diktieren."));
        _dictateMode = new FluentCombo { Width = ComboWidth, AccessibleName = "Modus" };
        FillChoices(_dictateMode, Core.Choices.DictateMode, settings.DictateMode);
        var dictateModeCard = new Card();
        dictateModeCard.AddRow(SettingRow("Modus", "", _dictateMode, ComboWidth, out _dictateModeText));
        // Selectable anyway (Mac): refusing the choice silently looked like a broken control. The note says why instead.
        _liveWarning = Block();
        var warningHead = new ThemedLabel { Text = "⚠", AutoSize = true, Tone = Tone.Caution, Margin = new Padding(0, 2, 0, 0) };
        _liveWarningText = Wrap("", 2 * RowPadding, small: false);
        _liveWarningText.Margin = new Padding(0, 2, 0, 2);
        ((ThemedLabel)_liveWarningText).Tone = Tone.Caution;
        var warningFoot = Wrap("Bis dahin starten das Diktieren-Kürzel und die rechte Strg-Taste nichts.", 2 * RowPadding, small: true);
        _useParakeet = new FluentButton("Parakeet verwenden") { AutoSize = true, Margin = new Padding(0, 4, 0, 4), AccessibleName = "Parakeet für Diktieren verwenden" };
        _useParakeet.Click += (_, _) =>
        {
            Core.Log.Info("SETTINGS: Live diktieren note — switched speech recognition to Parakeet on this PC");
            _speechEngine.SelectedItem = _speechEngine.Items.Cast<ChoiceItem<Core.Engine>>().First(i => i.Value == Core.Engine.Local);
            RefreshPickers();
        };
        _liveWarning.Controls.Add(warningHead);
        _liveWarning.Controls.Add(_liveWarningText);
        _liveWarning.Controls.Add(warningFoot);
        _liveWarning.Controls.Add(_useParakeet);
        dictateModeCard.AddRow(_liveWarning);
        _dictateMode.SelectedIndexChanged += (_, _) => UpdateDictateMode();
        dictate.Controls.Add(dictateModeCard);

        // --- transcription model: provider per step (Mac: one model picker that includes the offline models) ---
        dictate.Controls.Add(Section("Transkriptionsmodell", subtitle: "Welches Modell deine Aufnahme in Text verwandelt."));
        _speechEngine = EngineChoice(settings.SpeechEngine, "Spracherkennung", "Auf diesem PC (Parakeet)");
        var speechEngineCard = new Card();
        speechEngineCard.AddRow(SettingRow("Anbieter", "Wer die Aufnahme in Text verwandelt, auch die gesprochene Anweisung bei Dictate Prompt.",
            _speechEngine, ComboWidth, out _));
        var engineNote = Block();
        engineNote.Controls.Add(Wrap($"Gemischt geht auch: Die Spracherkennung auf diesem PC braucht nur {FormatBytes(ComponentSize(Core.OfflineCatalog.Parakeet))} "
            + "und ist sehr schnell, die Texte gehen online. Das große Sprachmodell muss dafür nicht geladen werden.", 2 * RowPadding, small: true));
        speechEngineCard.AddRow(engineNote);
        _dictateModel = ModelChoice(Core.GeminiModels.Transcription, settings.TranscriptionModel);
        _dictateGeminiCard = ModelCard(_dictateModel, "Gemini-Modell", "Gilt, wenn die Spracherkennung über Gemini läuft. "
            + "Auf diesem PC erkennt Parakeet die Sprache, ohne Modellwahl und ohne eigene Anweisungen.");
        _dictateModelOpenAi = ModelChoice(Core.OpenAiModels.Transcription, settings.OpenAiTranscriptionModel);
        _dictateOpenAiCard = ModelCard(_dictateModelOpenAi, "OpenAI-Modell", "Gilt, wenn die Spracherkennung über OpenAI läuft. "
            + "Das Glossar geht als Hinweis mit; die Anweisungen unten gelten nur für Gemini.");
        // The provider and model combos stay the stored state; the tiles (Mac model picker) set them.
        dictate.Controls.Add(Hidden(speechEngineCard, _dictateGeminiCard, _dictateOpenAiCard));
        dictate.Controls.Add(ModelPicker("Transkriptionsmodell", () => Core.ModelPickers.Transcription("Parakeet"),
            models => Core.ModelPickers.Recommended(models, Core.Settings.DefaultTranscriptionModel, Core.OpenAiModels.DefaultTranscriptionModel),
            _speechEngine, _dictateModel, _dictateModelOpenAi, () => OfflineEngine.SpeechInstalled));

        // --- offline speech model (Mac: Offline Models in this tab) ---
        dictate.Controls.Add(Section("Offline-Modell"));
        dictate.Controls.Add(Wrap("Läuft auf diesem PC, ohne Installation. Das Modell wird einmal geladen und bleibt gespeichert. "
            + $"Gemischt geht auch: Parakeet erkennt die Sprache, die Texte gehen online. Ablage: {Core.OfflineCatalog.Root}", 0));
        var speech = Core.OfflineCatalog.Parakeet;
        _speechRow = new ModelRow("Parakeet", "Für Diktieren und für gesprochene Anweisungen. Versteht Deutsch, Englisch und weitere Sprachen.", this);
        var speechCard = new Card();
        speechCard.AddRow(_speechRow);
        dictate.Controls.Add(speechCard);

        // Offline only (port of the Mac's Dictate → Punctuation): Gemini and OpenAI punctuate by themselves.
        var punctuationSection = Section("Satzzeichen", subtitle: "Wie Satzzeichen ins Diktat kommen.");
        dictate.Controls.Add(punctuationSection);
        _punctuation = new FluentCombo { Width = ComboWidth, AccessibleName = "Satzzeichen" };
        FillChoices(_punctuation, Core.Choices.Punctuation, settings.PunctuationMode);
        var punctuationCard = new Card();
        punctuationCard.AddRow(SettingRow("Satzzeichen", "", _punctuation, ComboWidth, out var punctuationText));
        void DescribePunctuation() => punctuationText.Text = SelectedPunctuation == Core.PunctuationMode.SpokenOnly
            ? "Nur gesagte Zeichen werden geschrieben: „Punkt“, „Komma“, „Fragezeichen“, „Ausrufezeichen“, „Doppelpunkt“ "
              + "und „Schrägstrich“. Am Ende kommt kein Punkt dazu."
            : "Parakeet setzt Punkt und Komma selbst.";
        _punctuation.SelectedIndexChanged += (_, _) => DescribePunctuation();
        DescribePunctuation();
        var punctuationNote = Block();
        punctuationNote.Controls.Add(Wrap("Gilt für die Spracherkennung auf diesem PC, auf Deutsch. Meetings und gesprochene Anweisungen "
            + "behalten die automatischen Satzzeichen.", 2 * RowPadding, small: true));
        punctuationCard.AddRow(punctuationNote);
        dictate.Controls.Add(punctuationCard);
        _punctuationOnly = new Control[] { punctuationSection, punctuationCard };

        // Mac Diktieren → Sprache (languageSection, WhisperLanguage). Only the OpenAI API takes a language.
        dictate.Controls.Add(Section("Sprache", subtitle: "In welcher Sprache du diktierst."));
        _language = new FluentCombo { Width = ComboWidth, AccessibleName = "Sprache" };
        foreach (var (code, name) in Core.TranscriptionLanguages.All) _language.Items.Add(new ChoiceItem<string>(code, name));
        _language.SelectedItem = _language.Items.Cast<ChoiceItem<string>>().First(i => i.Value == Core.TranscriptionLanguages.Normalize(settings.TranscriptionLanguage));
        var languageCard = new Card();
        languageCard.AddRow(SettingRow("Sprache", "Gilt nur für OpenAI: Bei „Automatisch erkennen“ bestimmt das Modell die Sprache selbst. "
            + "Gemini und Parakeet auf diesem PC erkennen die Sprache immer selbst und ignorieren diese Wahl.", _language, ComboWidth, out _));
        dictate.Controls.Add(languageCard);

        // --- glossary (Mac: in the Dictate tab) ---
        dictate.Controls.Add(Section("Glossar"));
        dictate.Controls.Add(Wrap("Ein Begriff pro Zeile: Namen, Medikamente, Fachwörter, die richtig geschrieben werden sollen. "
            + "Gilt auch offline; dort braucht die Spracherkennung nach einer Änderung einige Sekunden. Schneller geht es mit dem "
            + "Kürzel „Ins Glossar“ unter Allgemein.", 0));
        _glossary = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Glossar",
            Text = string.Join("\r\n", settings.Glossary ?? new List<string>()),
        };
        _glossaryCount = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 4, 0, 0) };
        _glossary.TextChanged += (_, _) => UpdateGlossaryCount();
        UpdateGlossaryCount();
        var glossaryBlock = Block();
        glossaryBlock.Controls.Add(new FieldFrame(_glossary, multiline: true) { Height = 220 });
        glossaryBlock.Controls.Add(_glossaryCount);
        var glossaryCard = new Card();
        glossaryCard.AddRow(glossaryBlock);
        dictate.Controls.Add(glossaryCard);

        dictate.Controls.Add(Section("System-Prompt"));
        dictate.Controls.Add(Wrap("So soll Gemini transkribieren (gilt nur für Gemini): Füllwörter, Satzzeichen, Formatierung. Namen und Fachwörter gehören "
            + "ins Glossar, nicht hierher. Der Standardtext ist englisch; geschrieben wird trotzdem in der gesprochenen Sprache.", 0));
        _dictatePrompt = new PromptEditor(Core.Prompts.DefaultTranscriptionSystemPrompt, settings.DictateSystemPrompt, "System-Prompt Diktieren");
        _dictateHistory = new HistoryLink(_dictatePrompt.Box, _history, Core.PromptField.Dictate);
        dictate.Controls.Add(PromptCard(_dictatePrompt, _dictateHistory));

        // Mac Diktieren → Advanced → „Genauigkeit“ (tuningSection): temperature and thinking for Gemini.
        _temperature = new FluentCombo { Width = ComboWidth, AccessibleName = "Temperatur" };
        foreach (var (value, label) in Core.TranscriptionTuning.Temperatures) _temperature.Items.Add(new ChoiceItem<double>(value, label));
        _temperature.SelectedItem = _temperature.Items.Cast<ChoiceItem<double>>()
            .First(i => i.Value == Core.TranscriptionTuning.NormalizeTemperature(settings.TranscriptionTemperature));
        _thinking = new FluentCombo { Width = ComboWidth, AccessibleName = "Denkaufwand" };
        foreach (var (value, label) in Core.TranscriptionTuning.ThinkingLevels) _thinking.Items.Add(new ChoiceItem<string>(value, label));
        _thinking.SelectedItem = _thinking.Items.Cast<ChoiceItem<string>>()
            .First(i => i.Value == Core.TranscriptionTuning.NormalizeThinking(settings.TranscriptionThinking));
        var tuningCard = new Card();
        tuningCard.AddRow(SettingRow("Temperatur", "Niedriger heißt wörtlicher. WhisperShortcut nutzt 0,0, das gibt wieder, was gesagt wurde. "
            + "Höhere Werte sind die häufigste Ursache für erfundene oder vertauschte Wörter.", _temperature, ComboWidth, out _));
        tuningCard.AddRow(SettingRow("Denkaufwand", "Mehr Denkaufwand hilft bei schwierigem Ton, Akzenten und ungewöhnlichem Vokabular, "
            + "kostet aber Zeit: bei Flash-Lite fast nichts, bei Pro etwa das Doppelte. Gemini 3.7/3.8 Flash und Pro laufen nicht unter "
            + "„Niedrig“ und werden darauf angehoben.", _thinking, ComboWidth, out _));
        AddAdvanced(dictate,
            Section("Genauigkeit", subtitle: "Wie frei das Modell vom Gehörten abweichen darf und wie lange es vorher nachdenken darf. "
                + "Gilt nur für Gemini; OpenAI und Parakeet nehmen diese Werte nicht.", first: true), tuningCard);

        // ================= page: Dictate Prompt =================
        prompt.Controls.Add(Section("Modell", subtitle: "Das Modell, das deine gesprochene Anweisung hört und auf den markierten Text anwendet."));
        _textEngine = EngineChoice(settings.TextEngine, "Texte bearbeiten", "Auf diesem PC (Sprachmodell)");
        var textEngineCard = new Card();
        textEngineCard.AddRow(SettingRow("Anbieter", "Wer die Anweisung auf den markierten Text anwendet, neue Texte schreibt und die "
            + "Schnellanweisungen ausführt.", _textEngine, ComboWidth, out _));
        _engineOnly = new Control[] { speechEngineCard, textEngineCard };
        _promptModel = ModelChoice(Core.GeminiModels.Prompt, settings.PromptModel);
        _promptGeminiCard = ModelCard(_promptModel, "Gemini-Modell", "Gilt, wenn Texte über Gemini bearbeitet werden. "
            + "Auf diesem PC arbeitet das Sprachmodell darunter.");
        _promptModelOpenAi = ModelChoice(Core.OpenAiModels.Prompt, settings.OpenAiPromptModel);
        _promptOpenAiCard = ModelCard(_promptModelOpenAi, "OpenAI-Modell", "Gilt, wenn Texte über OpenAI bearbeitet werden.");
        prompt.Controls.Add(Hidden(textEngineCard, _promptGeminiCard, _promptOpenAiCard));
        prompt.Controls.Add(ModelPicker("Modell für Dictate Prompt", () => Core.ModelPickers.Prompt(LocalLlmName),
            models => Core.ModelPickers.Recommended(models, Core.Settings.DefaultPromptModel, Core.OpenAiModels.DefaultPromptModel),
            _textEngine, _promptModel, _promptModelOpenAi, () => _llmChoice != null && OfflineEngine.LlmInstalled(SelectedLlmChoice)));

        // --- offline language model (Mac: the local models in the Dictate Prompt picker) ---
        prompt.Controls.Add(Section("Offline-Sprachmodell"));
        prompt.Controls.Add(Wrap("„Dictate Prompt“ dauert offline länger, je nach PC einige Sekunden bis eine Minute.", 0));
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
        prompt.Controls.Add(llmCard);
        _speechRow.Action.Click += (_, _) => ToggleDownload("speech", new[] { speech });
        _speechRow.Remove.LinkClicked += (_, _) => RemoveModel(speech, "die Offline-Spracherkennung");
        _llmRow.Action.Click += (_, _) => ToggleDownload("llm",
            new[] { Core.OfflineCatalog.LlamaServer(OfflineEngine.Arch), SelectedLlmChoice.Component });
        _llmRow.Remove.LinkClicked += (_, _) => RemoveModel(SelectedLlmChoice.Component, SelectedLlmChoice.DisplayName);
        _llmChoice.SelectedIndexChanged += (_, _) => { UpdateModelRows(); RefreshPickers(); };
        _offline.DownloadChanged += UpdateModelRows;
        Disposed += (_, _) => _offline.DownloadChanged -= UpdateModelRows;

        // --- screen content (Mac: Include screenshot) ---
        prompt.Controls.Add(Section("Bildschirminhalt", subtitle: "Schickt bei Dictate Prompt ein Bildschirmfoto mit, damit das Modell sieht, was du siehst."));
        _sendScreenshots = new ToggleSwitch { Checked = settings.SendScreenshots, AccessibleName = "Bildschirmfoto mitschicken" };
        var shotCard = new Card();
        shotCard.AddRow(SettingRow("Bildschirmfoto mitschicken", "Ein Bildschirmfoto (Kürzel unter Allgemein) geht einmal mit der nächsten "
            + "Anweisung oder Vorlage an das Sprachmodell in der Cloud und wird nach 5 Minuten verworfen. Achtung: Auf dem Bildschirm "
            + "der Praxissoftware stehen meist Patientendaten.", _sendScreenshots, ToggleWidth, out _));
        prompt.Controls.Add(shotCard);
        _screenshotOff = Wrap("Offline und mit dem Sprachmodell auf diesem PC geht kein Bild mit: Es würde den PC verlassen, "
            + "und das lokale Modell kann keine Bilder lesen.", 0);
        prompt.Controls.Add(_screenshotOff);

        prompt.Controls.Add(Section("Schnellanweisungen"));
        prompt.Controls.Add(Wrap($"Erscheinen bei Dictate Prompt ({Hotkey.Display(settings.PromptHotkey)}) unten in der Anzeige. "
            + $"{Core.QuickActionList.KeyRange} oder ein Klick wendet sie sofort an: auf die Markierung oder, ohne Markierung, auf das gerade "
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
        var quickReset = new LinkLabel { Text = "Vorschläge wiederherstellen", AutoSize = true, Margin = new Padding(0, 8, 0, 0), AccessibleName = "Vorschläge wiederherstellen" };
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

        prompt.Controls.Add(Section("Vorlage für neue Texte"));
        prompt.Controls.Add(Wrap("So soll ein Text aussehen, der ohne Markierung geschrieben wird: eine Behandlungsdokumentation, "
            + "ein Angebot, eine Mail. Was in [eckigen Klammern] steht, füllt das Modell nur, wenn es diktiert wurde. "
            + "Leer lassen = ohne Vorlage.", 0));
        _template = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Text = (settings.DictatePromptTemplate ?? "").ReplaceLineEndings("\r\n"), AccessibleName = "Vorlage für neue Texte",
        };
        var samples = new FluentButton("Beispiel einsetzen ▾") { AccessibleName = "Beispielvorlage einsetzen" };
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
        templateBlock.Controls.Add(Row(Wrap("Gilt nur, wenn nichts markiert ist. Regeln, die immer gelten sollen, gehören unten unter Anweisungen.",
            2 * RowPadding + 180, small: true), _templateHistory, samples));
        var templateCard = new Card();
        templateCard.AddRow(templateBlock);
        prompt.Controls.Add(templateCard);

        prompt.Controls.Add(Section("System-Prompt"));
        prompt.Controls.Add(Wrap("Grundregeln fürs Bearbeiten und Schreiben. Gelten immer: beim Bearbeiten einer Markierung, bei neuen Texten "
            + "und bei den Schnellanweisungen, online und offline. Hier hinein gehören feste Regeln wie Ton, Rechtschreibung oder "
            + "„nichts erfinden“. Der Standardtext ist englisch; geantwortet wird trotzdem in der Sprache des Textes.", 0));
        _promptPrompt = new PromptEditor(Core.Prompts.DefaultPromptModeSystemPrompt, settings.PromptModeSystemPrompt, "Anweisungen Dictate Prompt");
        _promptHistory = new HistoryLink(_promptPrompt.Box, _history, Core.PromptField.PromptMode);
        prompt.Controls.Add(PromptCard(_promptPrompt, _promptHistory));

        // ================= page: Vorlagen (dictation templates) =================
        _templates = (settings.Templates ?? new()).Select(t => t.Clone()).ToList();
        _templateKey = new HotkeyBox(settings.TemplateHotkey ?? "", allowEmpty: true) { AccessibleName = "Tastenkürzel Vorlage" };
        templatesPage.Controls.Add(KeyCard(_templateKey, "Wendet die zuletzt benutzte Vorlage an. Ins Feld klicken und die Kombination "
            + "drücken, z. B. Strg+Umschalt+V. Entf entfernt das Kürzel."));
        _templateKey.ValueChanged += UpdateTemplateState;

        templatesPage.Controls.Add(Section("Vorlagen"));
        _templateChoice = new FluentCombo { AccessibleName = "Vorlage auswählen" };
        var templateAdd = new FluentButton("Vorlage hinzufügen");
        _templateRemove = new FluentButton("Löschen");
        templateAdd.Click += (_, _) => AddTemplate();
        _templateRemove.Click += (_, _) => RemoveTemplate();
        _templateChoice.SelectedIndexChanged += (_, _) => { if (!_loadingTemplate) LoadTemplate(); };
        var listBlock = Block();
        listBlock.Controls.Add(Row(_templateChoice, templateAdd, _templateRemove));
        _templateEmpty = Wrap("Noch keine Vorlage. „Vorlage hinzufügen“ legt eine an.", 2 * RowPadding, small: true);
        listBlock.Controls.Add(_templateEmpty);
        var listCard = new Card();
        listCard.AddRow(listBlock);
        templatesPage.Controls.Add(listCard);

        _templateName = new TextBox { AccessibleName = "Name der Vorlage", PlaceholderText = "z. B. Behandlungsnotiz", MaxLength = 60 };
        _templateInstructions = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            AccessibleName = "Anweisungen der Vorlage",
            PlaceholderText = "z. B.: " + TemplateExample,
        };
        _templateOwnKey = new HotkeyBox("", allowEmpty: true) { AccessibleName = "Eigenes Tastenkürzel der Vorlage" };
        _templateState = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 4, 0, 0) };
        Fit(_templateState, 2 * RowPadding + 160);
        _templateName.TextChanged += (_, _) => TemplateEdited();
        _templateInstructions.TextChanged += (_, _) => TemplateEdited();
        _templateOwnKey.ValueChanged += TemplateEdited;
        var example = new LinkLabel { Text = "Beispiel einsetzen", AutoSize = true, Margin = new Padding(0, 4, 0, 0), Anchor = AnchorStyles.Right };
        example.LinkClicked += (_, _) => InsertTemplateExample();

        _templateEditor = new Card();
        _templateEditor.AddRow(SettingRow("Name", "Steht während der Aufnahme in der Anzeige.",
            new FieldFrame(_templateName) { Width = ComboWidth, Dock = DockStyle.None }, ComboWidth, out _));
        var formatBlock = Block();
        formatBlock.Controls.Add(new ThemedLabel { Text = "Format", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        formatBlock.Controls.Add(Wrap("Was gesagt wird, wird wie gewohnt erkannt und dann in dieses Format gebracht. Nur diese Anweisungen gelten: "
            + "nicht die Anweisungen, die Vorlage für neue Texte oder die Schnellanweisungen von Dictate Prompt.", 2 * RowPadding, small: true));
        formatBlock.Controls.Add(new FieldFrame(_templateInstructions, multiline: true) { Height = 240 });
        var formatFooter = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        formatFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        formatFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _templateState.Anchor = AnchorStyles.Left;
        formatFooter.Controls.Add(_templateState, 0, 0);
        formatFooter.Controls.Add(example, 1, 0);
        formatBlock.Controls.Add(formatFooter);
        _templateEditor.AddRow(formatBlock);
        _templateEditor.AddRow(SettingRow("Eigenes Kürzel (optional)", "Startet genau diese Vorlage, z. B. über eine Taste am Diktiermikrofon. "
            + "Meist reicht das Kürzel oben. Entf entfernt es.",
            new FieldFrame(_templateOwnKey) { Width = KeyWidth, Dock = DockStyle.None }, KeyWidth, out _));
        templatesPage.Controls.Add(_templateEditor);
        FillTemplateChoice(Math.Max(0, _templates.FindIndex(t => t.Id == settings.LastTemplateId)));

        // ================= page: Bildschirmfoto (Mac: How it works; saving to a folder does not exist on Windows) =================
        screenshotPage.Controls.Add(Section("So geht's", first: true));
        var shotKeyShown = new ThemedLabel { AutoSize = true, Font = Theme.Strong, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        void ShowShotKey() => shotKeyShown.Text = Hotkey.TryParse(_screenshotKey.Value, out var k) ? k.ToDisplayString() : "Nicht festgelegt";
        _screenshotKey.ValueChanged += ShowShotKey;
        ShowShotKey();
        var changeShotKey = new LinkLabel { Text = "Ändern …", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Kürzel für Bildschirmfoto ändern" };
        changeShotKey.LinkClicked += (_, _) => { ShowPage(General); _screenshotKey.Focus(); };
        var shotKeyLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        shotKeyLine.Controls.Add(shotKeyShown);
        shotKeyLine.Controls.Add(changeShotKey);
        var shotHowCard = new Card();
        shotHowCard.AddRow(SettingRow("Tastenkürzel", null, shotKeyLine, ComboWidth, out _));
        var shotHow = Block();
        shotHow.Controls.Add(Wrap("Kürzel drücken und einen Bereich aufziehen. Das Bild landet in der Zwischenablage und lässt sich überall "
            + "mit Strg+V einfügen.", 2 * RowPadding));
        shotHow.Controls.Add(Wrap("Ist unter Dictate Prompt „Bildschirmfoto mitschicken“ an, geht es außerdem einmal mit der nächsten "
            + "Anweisung oder Vorlage an das Sprachmodell.", 2 * RowPadding));
        shotHowCard.AddRow(shotHow);
        screenshotPage.Controls.Add(shotHowCard);

        // ================= page: Datenschutz & Berechtigungen =================
        privacy.Controls.Add(Section("Offline-Modus", first: true));
        _onlineMode = new OptionButton("Online") { Checked = !settings.OfflineMode, AccessibleName = "Arbeitsweise Online" };
        _offlineMode = new OptionButton("Offline auf diesem PC") { Checked = settings.OfflineMode, AccessibleName = "Arbeitsweise Offline auf diesem PC" };
        // Each option sits in its own card row, so the automatic radio grouping (same parent) does not apply.
        _onlineMode.CheckedChanged += (_, _) => { if (_onlineMode.Checked) _offlineMode.Checked = false; };
        _offlineMode.CheckedChanged += (_, _) => { if (_offlineMode.Checked) _onlineMode.Checked = false; };
        // Mac: one switch. The two option buttons stay the stored state (Snapshot, SyncOfflineFromTray) without being shown.
        var offlineSwitch = new ToggleSwitch { Checked = settings.OfflineMode, AccessibleName = "Offline-Modus" };
        offlineSwitch.CheckedChanged += (_, _) => (offlineSwitch.Checked ? _offlineMode : _onlineMode).Checked = true;
        _offlineMode.CheckedChanged += (_, _) => offlineSwitch.Checked = _offlineMode.Checked;
        var modeCard = new Card();
        modeCard.AddRow(SettingRow("Offline-Modus", "Alles läuft auf diesem PC: Diktat und Dictate Prompt nutzen die Modelle auf "
            + "diesem PC, keine Anfrage verlässt den Rechner, egal was sonst eingestellt ist. Gebaut für Diktate mit Patientendaten, bei "
            + "denen „die Aufnahme verlässt dieses Gerät nie“ gelten muss. Die Modelle werden einmal geladen (Diktieren, Dictate Prompt).",
            offlineSwitch, ToggleWidth, out _));
        privacy.Controls.Add(modeCard);
        _offlineState = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 4, 0, 4) };
        Fit(_offlineState, 0);
        privacy.Controls.Add(_offlineState);
        _onlineMode.CheckedChanged += (_, _) =>
        {
        UpdateMode();
            RefreshPickers();
            // Switching to online: say right away whether the stored keys work.
            if (_onlineMode.Checked && IsHandleCreated) CheckVisibleKeys();
        };
        _speechEngine.SelectedIndexChanged += (_, _) => UpdateMode();
        _textEngine.SelectedIndexChanged += (_, _) => UpdateMode();
        // „Schlüssel nötig“ on the tiles follows the key fields on Konten.
        _geminiKey.Box.TextChanged += (_, _) => RefreshPickers();
        _openAiKey.Box.TextChanged += (_, _) => RefreshPickers();

        privacy.Controls.Add(Section("Berechtigungen", subtitle: "Worauf WhisperShortcut auf diesem PC zugreift."));
        var micPrivacy = new FluentButton("Windows-Einstellungen öffnen") { MinimumSize = new Size(ButtonWidth + 100, 32), AccessibleName = "Mikrofon-Datenschutz in Windows öffnen" };
        micPrivacy.Click += (_, _) => OpenLocal("ms-settings:privacy-microphone");
        var permissionsCard = new Card();
        permissionsCard.AddRow(SettingRow("Mikrofon (erforderlich)", "Nimmt auf, was du für Diktieren und Dictate Prompt sagst. Ist der Zugriff "
            + "gesperrt, unter Datenschutz → Mikrofon „Desktop-Apps den Zugriff erlauben“ einschalten.", micPrivacy, ButtonWidth + 100, out _));
        privacy.Controls.Add(permissionsCard);

        // ================= page: Vorlesen (SettingsForm.ReadAloud.cs) =================
        BuildReadAloudPage();

        // ================= page: Chat (SettingsForm.Chat.cs) =================
        BuildChatPage();

        // ================= page: Meetings (SettingsForm.Meetings.cs) =================
        BuildMeetingsPage();

        // ================= page: Claude & KI-Apps (SettingsForm.AiApps.cs) =================
        BuildAiAppsPage(mcp);

        // ================= page: Info =================
        BuildInfoPage();

        // ================= frame: navigation, page host, buttons =================
        // Scrolls when the window is lower than the pages and their section headers.
        var nav = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(8, 12, 8, 8), AutoScroll = true };
        var logo = new PictureBox
        {
            Image = new Icon(TrayIcons.App, 64, 64).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(20, 18), Size = new Size(20, 20),
            AccessibleRole = AccessibleRole.None, TabStop = false,
        };
        var appTitle = new ThemedLabel
        {
            Text = "WhisperShortcut", AutoSize = false, Location = new Point(48, 16), Size = new Size(200, 24),
            Font = Theme.AppTitle, TextAlign = ContentAlignment.MiddleLeft,
        };
        nav.Controls.Add(logo);
        nav.Controls.Add(appTitle);
        int y = 56;
        string? section = null;
        foreach (var (name, icon, pageSection) in Pages)
        {
            if (pageSection != section)
            {
                // Mac sidebar section header: small secondary text above its group, not a page.
                if (section != null) y += 8;
                section = pageSection;
                nav.Controls.Add(new ThemedLabel
                {
                    Text = pageSection, Tone = Tone.Secondary, Font = Theme.Small, AutoSize = false,
                    Location = new Point(22, y), Size = new Size(240, 22), TextAlign = ContentAlignment.BottomLeft,
                    AccessibleRole = AccessibleRole.StaticText, UseMnemonic = false,
                });
                y += 24;
            }
            // 262 wide: leaves room for the vertical scroll bar, so a low window never scrolls sideways.
            var item = new NavItem(name, icon) { Location = new Point(8, y), Size = new Size(262, 38) };
            item.Picked += (_, _) => ShowPage(name);
            _nav.Add(item);
            nav.Controls.Add(item);
            y += 40;
        }

        _pageHost = new Panel { Dock = DockStyle.Fill };
        foreach (var page in _pages.Values) _pageHost.Controls.Add(page);

        // No Speichern (Mac): edits apply by themselves. The line below says when a shortcut could not be taken over.
        _dirty = new ThemedLabel { Text = "", Tone = Tone.Secondary, AutoSize = true, Visible = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        var version = new ThemedLabel { Text = $"Version {AppVersion}", Tone = Tone.Secondary, Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
        var buttons = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(28, 6, 20, 8) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_dirty, 0, 0);
        buttons.Controls.Add(version, 1, 0);

        var content = new Panel { Dock = DockStyle.Fill };
        // Docking runs in reverse order of adding: buttons take the bottom, the page the rest.
        content.Controls.Add(_pageHost);
        content.Controls.Add(buttons);
        Controls.Add(content);
        Controls.Add(nav);
        // Pages stay suspended until first shown (ShowPage): laying out all twelve up front cost seconds on open.
        ResumeLayout(false);
        PerformLayout();

        // The wheel moves the page on over a prompt box at its end and never changes a closed drop-down.
        WheelToPage.Attach(this);

        // Hints wrap at the visible page width, never wider.
        _pageHost.ClientSizeChanged += (_, _) => { FitHints(); foreach (var p in _pickers) p.Tiles.Parent?.PerformLayout(); };

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
            _openedPrompts = (_settings.DictateSystemPrompt, _settings.PromptModeSystemPrompt, _settings.DictatePromptTemplate);
        };
        UpdateMode();
        UpdateModelRows();
        ShowPage(General);
        Shown += (_, _) =>
        {
            var emptyKey = new[] { _geminiKey, _openAiKey }.FirstOrDefault(k => k.Card.Visible && k.Value.Length == 0);
            (emptyKey != null && _onlineMode.Checked ? (Control)emptyKey.Box : _nav[0]).Focus();
            if (_onlineMode.Checked) CheckVisibleKeys();
        };

        // Any edit is written once it held still for ApplyDelayMs (Mac: no Speichern, keys 500 ms after the last keystroke).
        var dirtyTimer = _applyTimer;
        // WHY skipped while a combo is open: Win32 reports the hot-tracked item as the selection, so hovering would apply it.
        var combos = new[] { _mic, _llmChoice, _dictateModel, _promptModel, _speechEngine, _textEngine, _dictateModelOpenAi, _promptModelOpenAi, _templateChoice, _punctuation, _dictateMode, _afterPaste, _language, _temperature, _thinking };
        dirtyTimer.Tick += (_, _) =>
        {
            if (_snapshot.Length == 0 || combos.Any(c => c.DroppedDown)) return;
            var now = Snapshot();
            if (now != _snapshot)
            {
                if (now != _pendingSnapshot) (_pendingSnapshot, _pendingSince) = (now, Environment.TickCount64);
                else if (Environment.TickCount64 - _pendingSince >= ApplyDelayMs) TrySave();
            }
            UpdateSaveState();
        };
        // WHY only while visible: the window built in advance waits hidden for hours, and reading every field
        // (prompts, glossary, templates) seven times a second there only costs CPU while the user dictates.
        VisibleChanged += (_, _) => dirtyTimer.Enabled = Visible;
        FormClosing += OnClosing;
        Activated += (_, _) => { if (!IsDisposed && !_discard) SyncOfflineFromTray(); };
        // A running mic test keeps the microphone (and the Windows mic indicator) busy in the background.
        Deactivate += (_, _) => { if (_probe != null) StopMicTest(); };
        // Light/dark switched in Windows while the window is open: follow it.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        FormClosed += (_, _) =>
        {
            if (!_discard) RecordPromptHistory(_openedPrompts.Dictate, _openedPrompts.PromptMode, _openedPrompts.Template);
        };
        // WHY on Disposed and not on FormClosed: a window built in advance that went stale is disposed without ever
        // being closed. Its subscriptions kept it alive and ran its handlers (download ticks, theme switches) forever.
        Disposed += (_, _) =>
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
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General
                or Microsoft.Win32.UserPreferenceCategory.Color
                or Microsoft.Win32.UserPreferenceCategory.Accessibility)
            || IsDisposed || !IsHandleCreated) return;
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
        if (page.Controls.Count > 0 && page.Controls[0] is TableLayoutPanel root && _building.Remove(root)) root.ResumeLayout(false);
        // Suspended: a page becoming visible otherwise lays itself out once per container (see SetVisibleCore).
        LayoutBatch.Run(page, () => { foreach (var p in _pages.Values) p.Visible = p == page; });
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
        // WHY suspended: an auto-size page re-lays out on every added row; the constructor resumes it once at the end.
        root.SuspendLayout();
        _building.Add(root);
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
        // Mac #346: Über holds information only (what it is, support, credits). Updates and Daten & Zurücksetzen
        // live on Allgemein, the Einführung in the tray menu („Einführung …“).
        var info = NewPage(InfoPage, null);
        var head = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(RowPadding, 14, RowPadding, 14), Margin = new Padding(0) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.Controls.Add(new PictureBox { Image = new Icon(TrayIcons.App, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 16, 0), AccessibleRole = AccessibleRole.None, TabStop = false }, 0, 0);
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
        info.Controls.Add(headCard);

        info.Controls.Add(Section("Hilfe und Entwickler"));
        var links = new Card();
        links.AddRow(SettingRow("Entwickelt von Magnus Goedde", "github.com/mgsgde/whisper-shortcut-windows",
            LinkButton("GitHub", "https://github.com/mgsgde/whisper-shortcut-windows"), ButtonWidth, out _));
        // Mac #344 „Meine Einstellungen senden“: sits with the contact ways because it is one.
        var sendSettings = new FluentButton("Senden …") { MinimumSize = new Size(96, 32), AccessibleName = "Meine Einstellungen senden" };
        sendSettings.Click += (_, _) => FeedbackReportDialog.Open(this, _settings, _mcp);
        links.AddRow(SettingRow("Meine Einstellungen senden", "Teile deine Einstellungen mit dem Entwickler, wenn etwas nicht "
            + "funktioniert hat: Version, Modelle, Tastenkürzel und welche Anweisungen geändert sind, nie ein Schlüssel. Du siehst "
            + "vorher alles und kannst es bearbeiten.", sendSettings, 96, out _));
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
        info.Controls.Add(Wrap("Offline-Spracherkennung: NVIDIA Parakeet TDT 0.6B v3, lizenziert unter CC BY 4.0, "
            + "als int8-Umrechnung von csukuangfj, ausgeführt mit sherpa-onnx (Apache 2.0). "
            + "Offline-Sprachmodell: Qwen (Alibaba) über Unsloth-GGUF, ausgeführt mit llama.cpp (MIT).", 0));
        var licenseLinks = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        licenseLinks.Controls.Add(LinkButton("NVIDIA Parakeet", "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"));
        licenseLinks.Controls.Add(LinkButton("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/"));
        licenseLinks.Controls.Add(LinkButton("sherpa-onnx", "https://github.com/k2-fsa/sherpa-onnx"));
        info.Controls.Add(licenseLinks);
    }

    /// <summary>Allgemein → Updates (Mac #346 moved it there from Über): manual check and the automatic-check switch.</summary>
    private void AddUpdatesSection(TableLayoutPanel page)
    {
        page.Controls.Add(Section("Updates", subtitle: "Ob WhisperShortcut nach neuen Versionen sucht."));
        var card = new Card();
        _updateButton = new FluentButton("Suchen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Nach Updates suchen" };
        _updateButton.Click += (_, _) =>
        {
            if (_updates.Available != null) _updates.ShowDialog();
            else _ = CheckUpdateAsync();
        };
        card.AddRow(SettingRow("Nach Updates suchen", "", _updateButton, ButtonWidth, out _updateStatus));
        _updateStatus.Text = "Fragt GitHub nach der neuesten Version. Installiert wird erst nach deinem Klick.";
        if (_updates.Available != null) ShowUpdateFound(_updates.Available);
        // The app builds this window in advance (TrayApp.ScheduleSpareSettings), before the first automatic check
        // a minute after start: a version found since then shows when the window does.
        VisibleChanged += (_, _) => { if (Visible && _updates.Available is { } found) ShowUpdateFound(found); };
        _autoUpdate = new ToggleSwitch { Checked = _settings.AutoUpdateCheck, AccessibleName = "Automatisch nach Updates suchen" };
        // The practice runs in offline mode, which promises that nothing leaves the PC: say plainly what this one request sends.
        card.AddRow(SettingRow("Automatisch nach Updates suchen", "Kurz nach dem Start und einmal am Tag. Eine neue Version meldet sich "
            + "mit einem Hinweis; installiert wird erst, wenn du auf „Jetzt installieren“ klickst. Auch im Offline-Modus: Die App fragt "
            + "dabei nur github.com nach der neuesten Versionsnummer, ohne Aufnahmen, Texte oder Einstellungen.", _autoUpdate, ToggleWidth, out _));
        page.Controls.Add(card);
    }

    /// <summary>Allgemein → Daten & Zurücksetzen, last on the page (Mac #346 moved it there from Über).</summary>
    private void AddDataSection(TableLayoutPanel page)
    {
        page.Controls.Add(Section("Daten & Zurücksetzen"));
        // Two folders on disk: %APPDATA% (settings, glossary, key) and %LOCALAPPDATA% (log and the Offline models).
        // Each row names what is really inside, so "Ordner öffnen" never surprises.
        var dataFolder = new FluentButton("Ordner öffnen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Ordner mit Einstellungen öffnen" };
        dataFolder.Click += (_, _) => OpenFolder(Core.AppData.SettingsFolder);
        var logFile = new FluentButton("Protokoll") { MinimumSize = new Size(96, 32), AccessibleName = "Protokolldatei öffnen" };
        logFile.Click += (_, _) =>
        {
            if (File.Exists(Core.Log.DefaultPath)) OpenLocal(Core.Log.DefaultPath);
            else OpenFolder(Core.AppData.LocalFolder);
        };
        var logFolder = new FluentButton("Ordner öffnen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Ordner mit Modellen und Protokoll öffnen" };
        logFolder.Click += (_, _) => OpenFolder(Core.AppData.LocalFolder);
        var logButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        logButtons.Controls.Add(logFile);
        logButtons.Controls.Add(logFolder);
        var reset = new FluentButton("Zurücksetzen …") { MinimumSize = new Size(ButtonWidth, 32), Danger = true, AccessibleName = "App zurücksetzen" };
        reset.Click += (_, _) => ResetApp();
        var uninstall = new FluentButton("Deinstallieren …") { MinimumSize = new Size(ButtonWidth, 32), Danger = true, AccessibleName = "WhisperShortcut deinstallieren" };
        uninstall.Click += (_, _) => StartUninstall();
        var dataCard = new Card();
        dataCard.AddRow(SettingRow("Einstellungen und Glossar", "Einstellungen, Glossar, eigene Anweisungen und der Schlüssel (verschlüsselt).",
            dataFolder, ButtonWidth, out _));
        dataCard.AddRow(SettingRow("Offline-Modelle und Protokoll", "Die geladenen Modelle (Unterordner Offline) und das Protokoll. "
            + "Das Protokoll hilft bei Fehlermeldungen und enthält keine diktierten Texte und keinen Schlüssel.", logButtons, ButtonWidth + 104, out _));
        dataCard.AddRow(SettingRow("App zurücksetzen", "Löscht alles, auch den API-Schlüssel, das Glossar und die Offline-Modelle. "
            + "Danach beendet sich WhisperShortcut und startet beim nächsten Mal wie neu installiert.", reset, ButtonWidth, out _));
        dataCard.AddRow(SettingRow("WhisperShortcut deinstallieren", "Entfernt die App ganz: Start mit Windows, Startmenü, Einstellungen, Schlüssel, "
            + "Glossar, Offline-Modelle und den Eintrag in Claude. Vorher fragt ein eigenes Fenster noch einmal nach.", uninstall, ButtonWidth, out _));
        page.Controls.Add(dataCard);
    }

    private static string AppVersion => Application.ProductVersion.Split('+')[0];

    /// <summary>Opens a prefilled GitHub issue; a body too long for the URL goes to the clipboard instead.</summary>
    internal void ReportProblem()
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
        OpenWeb(url);
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
            Warn(Core.UserCopy.TechnicalFallback(
                "Nicht alles konnte gelöscht werden, vermutlich ist eine Datei noch geöffnet. WhisperShortcut beendet sich jetzt; danach den App-Datenordner von Hand löschen.",
                e.Message));
        }
        _discard = true;
        Application.Exit();
    }

    /// <summary>
    /// Starts <c>"&lt;exe&gt;" --uninstall</c>, which asks, quits this app and removes everything. Nothing is closed here:
    /// cancelling in its dialog leaves the app as it is.
    /// </summary>
    private void StartUninstall()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uninstall") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e)
        {
            Core.Log.Error("Starting the uninstaller failed", e);
            Warn("Das Deinstallieren konnte nicht starten. Du findest WhisperShortcut auch unter Windows → Einstellungen → Apps.");
        }
    }

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception e) { Core.Log.Error($"Creating folder failed: {e.GetType().Name}"); }
        OpenLocal(path);
    }

    private FluentButton LinkButton(string text, string url)
    {
        var button = new FluentButton(text) { MinimumSize = new Size(ButtonWidth, 32) };
        button.Click += (_, _) => OpenWeb(url);
        return button;
    }

    /// <summary>Card with one row: "Tastenkürzel", a description, the hotkey field on the right.</summary>
    private Card KeyCard(HotkeyBox box, string description)
    {
        var card = new Card();
        card.AddRow(SettingRow("Tastenkürzel", description, new FieldFrame(box) { Width = KeyWidth, Dock = DockStyle.None }, KeyWidth, out _));
        return card;
    }

    /// <summary>Controls that hold state but are not shown (the provider and model combos behind the model tiles).</summary>
    private static Panel Hidden(params Control[] controls)
    {
        var holder = new Panel { Visible = false, Size = Size.Empty, Margin = new Padding(0) };
        holder.Controls.AddRange(controls);
        return holder;
    }

    /// <summary>The offline language model's name for the Dictate Prompt tile.</summary>
    private string LocalLlmName => _llmChoice?.SelectedItem is LlmItem item ? item.Llm.DisplayName : "Sprachmodell auf diesem PC";

    /// <summary>One row of the shortcut list: the function, an optional hint, the hotkey field on the right.</summary>
    private TableLayoutPanel KeyRow(string title, string? description, HotkeyBox box) =>
        SettingRow(title, description, new FieldFrame(box) { Width = KeyWidth, Dock = DockStyle.None }, KeyWidth, out _);

    private static FluentCombo ModelChoice(IReadOnlyList<Core.ModelInfo> models, string selected)
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

    private sealed record ModelItem(Core.ModelInfo Model)
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
            _menu.Items.Add(new ToolStripMenuItem("Einsetzen ersetzt den Text im Feld; er gilt sofort.") { Enabled = false });
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
        Text = text, AutoSize = true, Font = Theme.Title, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false,
    };

    // Mac SectionHeader: an accent symbol before each heading. Segoe Fluent Icons / MDL2 glyphs, by heading text.
    private static readonly Dictionary<string, string> SectionIcons = new()
    {
        ["Tastenkürzel"] = "\uE765", ["Start"] = "\uE7E8", ["Mikrofon"] = "\uE720", ["Zwischenablage"] = "\uE77F",
        ["Töne"] = "\uE767", ["Modus"] = "\uE8AC", ["Während einer Aufnahme"] = "\uE916", ["Transkriptionsmodell"] = "\uE8D6",
        ["Offline-Modell"] = "\uE896", ["Offline-Sprachmodell"] = "\uE896", ["Satzzeichen"] = "\uE8D2", ["Glossar"] = "\uE82D",
        ["System-Prompt"] = "\uE8BD", ["Modell"] = "\uE950", ["Bildschirminhalt"] = "\uE7F4", ["Schnellanweisungen"] = "\uE945",
        ["Vorlage für neue Texte"] = "\uE8A5", ["Vorlagen"] = "\uE8A5", ["So geht's"] = "\uE897", ["Offline-Modus"] = "\uE839",
        ["Berechtigungen"] = "\uEA18", ["Einführung"] = "\uE734", ["Daten & Zurücksetzen"] = "\uE7B8",
        ["Hilfe und Entwickler"] = "\uE897", ["Lizenz"] = "\uE8A5", ["Verbindung"] = "\uE71B", ["Was KI-Apps dürfen"] = "\uE72E",
        ["Mit Claude verbinden"] = "\uE8F2", ["Änderungen durch KI-Apps"] = "\uE81C",
        ["Google-API-Schlüssel"] = "\uE8D7", ["OpenAI-API-Schlüssel"] = "\uE8D7",
    };

    /// <summary>
    /// Section heading like the Mac's SectionHeader: accent symbol, heading and, when given, one sentence of subtitle.
    /// </summary>
    private Control Section(string text, bool first = false, string? subtitle = null)
    {
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        if (SectionIcons.TryGetValue(text, out var glyph))
            head.Controls.Add(new ThemedLabel { Text = glyph, Font = Theme.SectionIcon, Tone = Tone.Accent, AutoSize = true, Margin = new Padding(0, 3, 8, 0), UseMnemonic = false });
        head.Controls.Add(new ThemedLabel { Text = text, AutoSize = true, Font = Theme.Section, Margin = new Padding(0), UseMnemonic = false });
        var box = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
            Margin = new Padding(0, first ? 6 : 26, 0, 8), Padding = new Padding(0),
        };
        box.Controls.Add(head);
        if (subtitle != null)
        {
            var sub = Wrap(subtitle, 0);
            sub.Margin = new Padding(0, 4, 0, 0);
            box.Controls.Add(sub);
        }
        return box;
    }

    /// <summary>
    /// Mac AdvancedSettingsGroup: rarely needed settings at the end of a page, folded in until „Erweitert“ is clicked.
    /// </summary>
    private void AddAdvanced(TableLayoutPanel page, params Control[] items)
    {
        var toggle = new LinkLabel { Text = "▸ Erweitert", AutoSize = true, Margin = new Padding(0, 26, 0, 4), AccessibleName = "Erweiterte Einstellungen zeigen" };
        page.Controls.Add(toggle);
        foreach (var item in items)
        {
            item.Visible = false;
            page.Controls.Add(item);
        }
        toggle.LinkClicked += (_, _) =>
        {
            bool open = !items[0].Visible;
            foreach (var item in items) item.Visible = open;
            toggle.Text = open ? "▾ Erweitert" : "▸ Erweitert";
            toggle.AccessibleName = open ? "Erweiterte Einstellungen verbergen" : "Erweiterte Einstellungen zeigen";
            FitHints();
        };
    }

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

    /// <summary>
    /// A key the check confirmed is saved right away, without waiting for the automatic apply. True when newly saved.
    /// </summary>
    private bool SaveCheckedKey(string key, bool gemini)
    {
        var before = gemini ? _settings.ApiKey : _settings.OpenAiKey;
        if (key == before || _snapshot.Length == 0) return false;
        if (gemini) _settings.ApiKey = key; else _settings.OpenAiKey = key;
        try
        {
            _settings.Save();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Saving the checked key failed: {e.GetType().Name}");
            if (gemini) _settings.ApiKey = before; else _settings.OpenAiKey = before;
            return false;
        }
        Core.Log.Info($"{(gemini ? "Gemini" : "OpenAI")} key saved after a successful check");
        // The key is no longer an unsaved change; everything else keeps its state (fields as in Snapshot()).
        var parts = _snapshot.Split('\u0001');
        parts[gemini ? 2 : 3] = key;
        _snapshot = string.Join('\u0001', parts);
        _savedHintUntil = Environment.TickCount64 + 3000;
        UpdateSaveState();
        Saved?.Invoke();
        return true;
    }

    /// <summary>What the Google key check says (also used by the welcome tour).</summary>
    internal static (string Text, Tone Tone) GeminiKeyMessage(Core.KeyCheck result) => result switch
    {
        Core.KeyCheck.Ok => ("✓ Schlüssel funktioniert", Tone.Good),
        Core.KeyCheck.Invalid => ("✗ Google kennt diesen Schlüssel nicht. Bitte noch einmal kopieren, ohne Leerzeichen.", Tone.Bad),
        Core.KeyCheck.Forbidden => ("✗ Google kennt den Schlüssel, lässt ihn aber nicht an Gemini. In Google AI Studio einen neuen Schlüssel erstellen.", Tone.Bad),
        Core.KeyCheck.NoConnection => ("Keine Verbindung zu Google. Internet prüfen; gespeichert werden kann trotzdem.", Tone.Caution),
        _ => ("Google hat nicht eindeutig geantwortet. Später noch einmal prüfen.", Tone.Caution),
    };

    /// <summary>What the OpenAI key check says (also used by the welcome tour).</summary>
    internal static (string Text, Tone Tone) OpenAiKeyMessage(Core.KeyCheck result) => result switch
    {
        Core.KeyCheck.Ok => ("✓ Schlüssel funktioniert", Tone.Good),
        Core.KeyCheck.Invalid => ("✗ OpenAI kennt diesen Schlüssel nicht. Bitte noch einmal kopieren, ohne Leerzeichen.", Tone.Bad),
        Core.KeyCheck.Forbidden => ("✗ OpenAI kennt den Schlüssel, er darf aber keine Modelle benutzen. Rechte des Projekts prüfen.", Tone.Bad),
        Core.KeyCheck.NoConnection => ("Keine Verbindung zu OpenAI. Internet prüfen; gespeichert werden kann trotzdem.", Tone.Caution),
        _ => ("OpenAI hat nicht eindeutig geantwortet. Später noch einmal prüfen.", Tone.Caution),
    };

    /// <summary>One API key: the field, Anzeigen / Prüfen, and a check that runs while the key is pasted. Also used by the welcome tour.</summary>
    internal sealed class KeyField : IDisposable
    {
        public TextBox Box { get; }
        public ThemedLabel Status { get; } = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0), Text = "" };
        public Control Section { get; set; } = null!;
        public Card Card { get; set; } = null!;
        public string Value => Box.Text.Trim();

        private readonly Func<string, CancellationToken, Task<Core.KeyCheck>> _check;
        private readonly Func<Core.KeyCheck, (string Text, Tone Tone)> _message;
        private readonly Func<string, bool> _commit;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 700 };
        private CancellationTokenSource? _cts;

        /// <param name="commit">Saves a key the check confirmed; true when it was not saved before.</param>
        public KeyField(string value, Func<string, CancellationToken, Task<Core.KeyCheck>> check,
            Func<Core.KeyCheck, (string Text, Tone Tone)> message, Func<string, bool> commit)
        {
            _check = check;
            _message = message;
            _commit = commit;
            Box = new TextBox { UseSystemPasswordChar = true, Text = value };
            // Pasting a key is the moment to know whether it works.
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
            // Value may have changed while Google answered: only the key that was checked is saved.
            if (result == Core.KeyCheck.Ok && key == Value && _commit(key)) text = "✓ Schlüssel funktioniert und ist gespeichert";
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
        var show = new FluentButton("Anzeigen") { MinimumSize = new Size(100, 32), AccessibleName = title + " anzeigen oder verbergen" };
        show.Click += (_, _) =>
        {
            key.Box.UseSystemPasswordChar = !key.Box.UseSystemPasswordChar;
            show.Text = key.Box.UseSystemPasswordChar ? "Anzeigen" : "Verbergen";
        };
        var check = new FluentButton("Prüfen") { MinimumSize = new Size(100, 32), AccessibleName = title + " prüfen" };
        check.Click += (_, _) => _ = key.CheckAsync();
        var block = Block();
        block.Controls.Add(Row(new FieldFrame(key.Box), show, check));
        Fit(key.Status, 2 * RowPadding);
        block.Controls.Add(key.Status);
        block.Controls.Add(Wrap(note, 2 * RowPadding, small: true));
        var link = new LinkLabel { Text = linkText, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        link.LinkClicked += (_, _) => OpenWeb(url);
        block.Controls.Add(link);
        key.Card = new Card();
        key.Card.AddRow(block);
        page.Controls.Add(key.Card);
    }

    private static FluentCombo EngineChoice(Core.Engine selected, string name, string localLabel)
    {
        var combo = new FluentCombo { Width = ComboWidth, AccessibleName = name };
        foreach (var c in Core.Choices.Engine.All)
            combo.Items.Add(new ChoiceItem<Core.Engine>(c.Value, c.Value == Core.Engine.Local ? localLabel : c.Label));
        combo.SelectedItem = combo.Items.Cast<ChoiceItem<Core.Engine>>().First(i => i.Value == selected);
        return combo;
    }

    private static Core.Engine SelectedEngine(FluentCombo combo) => ((ChoiceItem<Core.Engine>)combo.SelectedItem!).Value;

    /// <summary>Fills a combo from a choice table (its order and labels) and selects <paramref name="selected"/>.</summary>
    private static void FillChoices<T>(FluentCombo combo, Core.ChoiceSet<T> set, T selected)
    {
        foreach (var c in set.All) combo.Items.Add(new ChoiceItem<T>(c.Value, c.Label));
        combo.SelectedItem = combo.Items.Cast<ChoiceItem<T>>().First(i => EqualityComparer<T>.Default.Equals(i.Value, selected));
    }

    /// <summary>A combo entry: the stored value and its German label.</summary>
    private sealed record ChoiceItem<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private string SelectedLanguage => ((ChoiceItem<string>)_language.SelectedItem!).Value;
    private double SelectedTemperature => ((ChoiceItem<double>)_temperature.SelectedItem!).Value;
    private string SelectedThinking => ((ChoiceItem<string>)_thinking.SelectedItem!).Value;

    private Core.ClipboardAfterPaste SelectedAfterPaste => ((ChoiceItem<Core.ClipboardAfterPaste>)_afterPaste.SelectedItem!).Value;

    private Core.PunctuationMode SelectedPunctuation => ((ChoiceItem<Core.PunctuationMode>)_punctuation.SelectedItem!).Value;

    private Core.DictateMode SelectedDictateMode => ((ChoiceItem<Core.DictateMode>)_dictateMode.SelectedItem!).Value;

    /// <summary>
    /// Modus caption, or the orange note when Live is chosen but cannot start (Mac liveUnavailableWarning). Recomputed on
    /// every model, provider, offline or download change, so switching the model below updates it at once.
    /// </summary>
    private void UpdateDictateMode()
    {
        if (IsDisposed) return;
        bool offlineSpeech = !_onlineMode.Checked || SelectedEngine(_speechEngine) == Core.Engine.Local;
        var reason = SelectedDictateMode == Core.DictateMode.Live
            ? Core.LiveDictation.UnavailableReason(duringMeeting: false, offlineSpeech, OfflineEngine.SpeechInstalled)
            : null;
        string caption = SelectedDictateMode == Core.DictateMode.Dictate
            ? "Der Text wird eingefügt, wenn du stoppst."
            : reason == null ? "Der Text erscheint nach jeder Pause am Cursor, offline transkribiert von Parakeet." : "";
        if (_dictateModeText.Text != caption) _dictateModeText.Text = caption;
        _dictateModeText.Visible = caption.Length > 0;
        if (reason is { } why)
        {
            // The message points to Einstellungen → Diktieren, which is this page (Mac: "Select it below").
            _liveWarningText.Text = why == Core.LiveUnavailable.NotParakeet
                ? "Live diktieren funktioniert mit dem Offline-Modell Parakeet. Wähle es unten aus."
                : Core.LiveDictation.Message(why);
            _useParakeet.Visible = why == Core.LiveUnavailable.NotParakeet && OfflineEngine.SpeechInstalled;
        }
        _liveWarning.Visible = reason != null;
    }

    // ---------------- microphone ----------------

    private sealed record MicItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _refillingMics;

    private void FillMicrophones(string? selected)
    {
        var names = Microphones.Names();
        _mic.Items.Add(new MicItem("", "Windows-Standard"));
        foreach (var name in names) _mic.Items.Add(new MicItem(name, name));
        var items = _mic.Items.Cast<MicItem>().ToList();
        int found = Core.MicMatch.Find(selected, names);
        var match = found >= 0
            ? items.First(i => i.Value == names[found])
            : items.FirstOrDefault(i => i.Value == (selected ?? ""));
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
        var names = Microphones.Names();
        var wanted = new List<MicItem> { new("", "Windows-Standard") };
        wanted.AddRange(names.Select(n => new MicItem(n, n)));
        int found = Core.MicMatch.Find(selected, names);
        if (found >= 0)
            selected = names[found];
        else if (!wanted.Any(i => i.Value == selected))
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
            Warn(e is MicrophoneUnavailableException ? e.Message : Core.UserCopy.TechnicalFallback("Das Mikrofon lässt sich nicht öffnen.", e.Message));
            return;
        }
        _micTest.Text = "Stopp";
        _micTest.AccessibleName = Core.UserCopy.MicTestStopAccessibleName;
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
        _micTest.AccessibleName = Core.UserCopy.MicTestAccessibleName;
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
        bool offlineSpeech = !online || SelectedEngine(_speechEngine) == Core.Engine.Local;
        foreach (var c in _punctuationOnly) c.Visible = offlineSpeech;
        UpdateDictateMode();
        bool openAiSpeech = online && SelectedEngine(_speechEngine) == Core.Engine.OpenAI;
        bool openAiText = online && SelectedEngine(_textEngine) == Core.Engine.OpenAI;
        (_dictateGeminiCard.Visible, _dictateOpenAiCard.Visible) = (!openAiSpeech, openAiSpeech);
        (_promptGeminiCard.Visible, _promptOpenAiCard.Visible) = (!openAiText, openAiText);
        bool shotsPossible = online && SelectedEngine(_textEngine) != Core.Engine.Local;
        _sendScreenshots.Enabled = _screenshotKey.Enabled = shotsPossible;
        _screenshotOff.Visible = !shotsPossible;
        _offlineState.Text = _offlineMode.Checked
            ? "Der Offline-Modus ist an: Aufnahmen und Texte bleiben auf diesem PC. Beides muss einmal geladen sein."
            : SelectedEngine(_speechEngine) == Core.Engine.Local && SelectedEngine(_textEngine) != Core.Engine.Local
                ? "Die Spracherkennung läuft auf diesem PC, Texte gehen online. Nur die Spracherkennung muss geladen sein."
            : SelectedEngine(_textEngine) == Core.Engine.Local && SelectedEngine(_speechEngine) != Core.Engine.Local
                ? "Texte werden auf diesem PC bearbeitet, die Spracherkennung läuft online. Dafür muss das Sprachmodell geladen sein."
            : "Der Offline-Modus ist aus. Die Modelle lassen sich schon vorher laden (Diktieren, Dictate Prompt).";
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
            Warn(Core.UserCopy.TechnicalFallback("Das Modell konnte nicht gelöscht werden.", e.Message));
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
        // WHY: this runs on every download progress tick. Rebuilding the tiles relayouts the whole page and froze the
        // window during a download; the tiles only show installed or not, so refresh only when that changes.
        var pickerState = (speechReady, llmReady, llm.Component.Id);
        if (pickerState != _pickerModelState)
        {
            _pickerModelState = pickerState;
            RefreshPickers();
            UpdateDictateMode(); // "Parakeet verwenden" appears once Parakeet is on this PC
        }
        _llmChoice.Enabled = download?.Id != "llm";
    }

    private void UpdateRow(ModelRow row, string id, bool installed, long size, OfflineEngine.Download? download)
    {
        bool mine = download?.Id == id;
        string? error = download == null && _offline.LastFinished is { Error: { } e } last && last.Id == id ? e : null;
        // WHY: this runs on every download tick. On a page that is not in front, setting Visible = true lays out the
        // whole page each time even when nothing changed (about 120 ms), so the window froze during a download.
        var state = (installed, mine, busy: download != null, error, size, percent: mine ? (int)Math.Round(download!.Fraction * 100) : -1);
        if (Equals(row.Shown, state)) return;
        row.Shown = state;
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
        /// <summary>The state last drawn (UpdateRow skips identical ticks).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public object? Shown { get; set; }
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
        // The AI-apps page refreshes while the constructor still builds the pages; the resize after the
        // window is laid out fits the hints then.
        if (_pageHost is null) return;
        // Page padding (72 at 96 dpi) and a possible scroll bar.
        int width = _pageHost.ClientSize.Width - LogicalToDeviceUnits(72) - SystemInformation.VerticalScrollBarWidth;
        var changed = _wraps
            .Select(w => (w.Label, Size: new Size(Math.Max(width - LogicalToDeviceUnits(w.Inset), LogicalToDeviceUnits(200)), 0)))
            .Where(w => w.Label.MaximumSize != w.Size)
            .ToList();
        if (changed.Count == 0) return;
        // WHY batched: every new MaximumSize re-lays out the nested auto-size tables of all pages, about a
        // hundred times on the first open (2 s). Batched, each container lays out once, children first.
        LayoutBatch.Run(_pageHost, () => { foreach (var (label, size) in changed) label.MaximumSize = size; });
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
        _dictatePrompt.Value, _promptPrompt.Value, _autoPaste.Checked.ToString(), SelectedAfterPaste.ToString(), _sounds.Checked.ToString(),
        _autoUpdate.Checked.ToString(), _templateKey.Value, _sendScreenshots.Checked.ToString(), _screenshotKey.Value, _liveKey.Value,
        SelectedPunctuation.ToString(), SelectedDictateMode.ToString(), _settingsKey.Value, _rightCtrl.Checked.ToString(), ReadAloudSnapshot(), ChatSnapshot(), MeetingsSnapshot(),
        SelectedLanguage, Core.TranscriptionTuning.Format(SelectedTemperature), SelectedThinking,
    }.Concat(_quick.Select(q => q.Text))
     .Concat(_templates.Select(t => string.Join("\u0002", t.Id, t.Name, t.Instructions, t.Hotkey))));

    private static string SelectedModel(FluentCombo combo) => ((ModelItem)combo.SelectedItem!).Model.Id;

    private static Keys ToKeys(Hotkey h) => h.Key
        | ((h.Modifiers & NativeMethods.MOD_CONTROL) != 0 ? Keys.Control : 0)
        | ((h.Modifiers & NativeMethods.MOD_ALT) != 0 ? Keys.Alt : 0)
        | ((h.Modifiers & NativeMethods.MOD_SHIFT) != 0 ? Keys.Shift : 0);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsPageShortcut(keyData, out int step))
        {
            ShowAdjacentPage(step);
            return true;
        }
        if (ActiveControl is NavItem && keyData is Keys.Down or Keys.Right)
        {
            ShowAdjacentPage(1);
            return true;
        }
        if (ActiveControl is NavItem && keyData is Keys.Up or Keys.Left)
        {
            ShowAdjacentPage(-1);
            return true;
        }
        // The open-settings hotkey closes the window again (Mac toggleSettings). The global hotkeys are off while this
        // window is in front, so the window handles the key itself; a hotkey field records it instead.
        if (ActiveControl is not HotkeyBox && Hotkey.TryParse(_settings.ActiveOpenSettingsHotkey, out var toggle)
            && keyData == ToKeys(toggle))
        {
            Close();
            return true;
        }
        // Esc out of habit must not throw away typed glossary terms: close the way the X does.
        // Hotkey fields record Esc themselves, an open dropdown closes itself.
        if (keyData == Keys.Escape && ActiveControl is not HotkeyBox && ActiveControl is not ComboBox { DroppedDown: true })
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsPageShortcut(Keys keyData, out int step)
    {
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Next))
        {
            step = 1;
            return true;
        }
        if (keyData is (Keys.Control | Keys.Shift | Keys.Tab) or (Keys.Control | Keys.PageUp))
        {
            step = -1;
            return true;
        }
        step = 0;
        return false;
    }

    private void ShowAdjacentPage(int step)
    {
        var names = Pages.Select(p => p.Name).ToArray();
        int current = Array.FindIndex(names, n => _nav.Any(item => item.Selected && item.Text == n));
        if (current < 0) current = 0;
        int next = Core.UserCopy.NextPage(current, names.Length, step);
        ShowPage(names[next]);
        if (next >= 0 && next < _nav.Count) _nav[next].Focus();
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
        if (_discard || _snapshot.Length == 0) return;
        if (Snapshot() != _snapshot) TrySave();
        if (_keyProblem == null || e.CloseReason != CloseReason.UserClosing) return;
        if (ChoiceDialog.Ask(this, "Kürzel nicht übernommen", _keyProblem + " Es gelten weiter die bisherigen Kürzel.",
                new[] { "Trotzdem schließen", "Zurück" }) != 0)
            e.Cancel = true;
    }

    /// <summary>Status line at the bottom: a shortcut that was not taken over, just saved, or nothing.</summary>
    private void UpdateSaveState()
    {
        var (text, tone, visible) = _keyProblem != null ? ("⚠ " + _keyProblem, Tone.Bad, true)
            : _lastSaveError != null ? ("⚠ " + _lastSaveError, Tone.Bad, true)
            : ("✓ Gespeichert", Tone.Good, Environment.TickCount64 < _savedHintUntil);
        if (_dirty.Text != text) _dirty.Text = text;
        _dirty.Tone = tone;
        _dirty.Visible = visible;
    }

    /// <summary>Why the shortcuts cannot be taken over as they are, or null. <paramref name="parsed"/>: Diktieren, Dictate Prompt, Ins Glossar.</summary>
    private string? KeyProblem(out Hotkey?[] parsed)
    {
        // Diktieren and Dictate Prompt need a key; Ins Glossar may be empty (= off).
        var keys = new[] { _dictateKey, _promptKey, _glossaryKey };
        parsed = new Hotkey?[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == _glossaryKey && _glossaryKey.Value.Length == 0) continue;
            if (!Hotkey.TryParse(keys[i].Value, out var key) || !IsSafe(key))
                return "Bitte für Diktieren und Dictate Prompt ein Tastenkürzel festlegen, z. B. Alt+1 oder Strg+Umschalt+D. "
                    + "Strg+Alt allein geht nicht, das ist auf deutschen Tastaturen AltGr.";
            parsed[i] = key;
        }
        bool hasSettingsKey = Hotkey.TryParse(_settingsKey.Value, out var settingsKey);
        if (_settingsKey.Value.Length > 0 && (!hasSettingsKey || !IsSafe(settingsKey)))
            return "Kürzel für Einstellungen öffnen: bitte z. B. Alt+0 oder Strg+Umschalt+E festlegen, oder es mit Entf entfernen.";
        for (int i = 0; i < keys.Length; i++)
        {
            if (parsed[i] is not { } key) continue;
            if (parsed.Take(i).Contains(key) || (hasSettingsKey && key == settingsKey))
                return "Die Tastenkürzel müssen verschieden sein. Diktieren, Dictate Prompt, Ins Glossar und Einstellungen öffnen brauchen je ein eigenes.";
        }
        return TemplateKeyProblem(parsed);
    }

    /// <summary>
    /// Writes the window's state to the settings (called shortly after each edit, and on close). Shortcuts that are
    /// invalid or taken twice are not taken over (<see cref="_keyProblem"/>); everything else is.
    /// </summary>
    private bool TrySave()
    {
        _keyProblem = KeyProblem(out var parsed);
        bool keysOk = _keyProblem == null;
        var before = (_settings.OfflineMode, _settings.OfflineLlmId, _settings.ApiKey, _settings.Microphone,
            _settings.DictateHotkey, _settings.PromptHotkey, _settings.GlossaryHotkey, _settings.Glossary,
            _settings.QuickActions, _settings.DictatePromptTemplate, _settings.TranscriptionModel, _settings.PromptModel,
            _settings.DictateSystemPrompt, _settings.PromptModeSystemPrompt, _settings.AutoPaste, _settings.ClipboardAfterPaste, _settings.Sounds,
            _settings.OpenAiKey, _settings.SpeechEngine, _settings.TextEngine, _settings.OpenAiTranscriptionModel, _settings.OpenAiPromptModel,
            _settings.AutoUpdateCheck, _settings.Templates, _settings.TemplateHotkey, _settings.LastTemplateId,
            _settings.SendScreenshots, _settings.ScreenshotHotkey, _settings.LiveDictateHotkey, _settings.PunctuationMode,
            _settings.GlossaryHotkeyEnabled, _settings.ScreenshotHotkeyEnabled, _settings.LiveDictateHotkeyEnabled,
            _settings.OpenSettingsHotkey, _settings.OpenSettingsHotkeyEnabled, _settings.RightCtrlDictation,
            _settings.TranscriptionLanguage, _settings.TranscriptionTemperature, _settings.TranscriptionThinking, _settings.DictateMode);
        _settings.OfflineMode = _offlineMode.Checked;
        _settings.OfflineLlmId = SelectedLlmChoice.Id;
        _settings.ApiKey = _geminiKey.Value;
        _settings.OpenAiKey = _openAiKey.Value;
        _settings.SpeechEngine = SelectedEngine(_speechEngine);
        _settings.TextEngine = SelectedEngine(_textEngine);
        _settings.OpenAiTranscriptionModel = SelectedModel(_dictateModelOpenAi);
        _settings.OpenAiPromptModel = SelectedModel(_promptModelOpenAi);
        _settings.Microphone = SelectedMicrophone;
        if (keysOk)
        {
            _settings.DictateHotkey = parsed[0]!.Value.ToString();
            _settings.PromptHotkey = parsed[1]!.Value.ToString();
            (_settings.GlossaryHotkey, _settings.GlossaryHotkeyEnabled) = StoredKey(_glossaryKey.Value, _settings.GlossaryHotkey);
            (_settings.ScreenshotHotkey, _settings.ScreenshotHotkeyEnabled) = StoredKey(_screenshotKey.Value, _settings.ScreenshotHotkey);
            (_settings.LiveDictateHotkey, _settings.LiveDictateHotkeyEnabled) = StoredKey(_liveKey.Value, _settings.LiveDictateHotkey);
            (_settings.OpenSettingsHotkey, _settings.OpenSettingsHotkeyEnabled) = StoredKey(_settingsKey.Value, _settings.OpenSettingsHotkey);
            _settings.TemplateHotkey = Hotkey.TryParse(_templateKey.Value, out var picker) ? picker.ToString() : "";
        }
        // Names, instructions and the list itself are saved even when a key is invalid; only the keys are held back.
        _settings.Templates = Core.TemplateSave.Build(_templates, _settings.Templates, keysOk,
            k => Hotkey.TryParse(k, out var own) ? own.ToString() : null);
        _settings.Glossary = _glossary.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        _settings.QuickActions = Core.QuickActionList.Clean(_quick.Select(q => q.Text));
        _settings.DictatePromptTemplate = _template.Text.Trim().Length == 0 ? "" : _template.Text.ReplaceLineEndings("\n").Trim();
        _settings.TranscriptionModel = SelectedModel(_dictateModel);
        _settings.PromptModel = SelectedModel(_promptModel);
        _settings.DictateSystemPrompt = _dictatePrompt.Value;
        _settings.PromptModeSystemPrompt = _promptPrompt.Value;
        _settings.AutoPaste = _autoPaste.Checked;
        _settings.ClipboardAfterPaste = SelectedAfterPaste;
        _settings.PunctuationMode = SelectedPunctuation;
        _settings.DictateMode = SelectedDictateMode;
        _settings.TranscriptionLanguage = SelectedLanguage;
        _settings.TranscriptionTemperature = SelectedTemperature;
        _settings.TranscriptionThinking = SelectedThinking;
        _settings.Sounds = _sounds.Checked;
        _settings.AutoUpdateCheck = _autoUpdate.Checked;
        _settings.SendScreenshots = _sendScreenshots.Checked;
        _settings.RightCtrlDictation = _rightCtrl.Checked;
        var readAloudBefore = ApplyReadAloud(keysOk);
        var meetingsBefore = ApplyMeetings();
        var chatBefore = ApplyChat(keysOk);
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
                _settings.DictateSystemPrompt, _settings.PromptModeSystemPrompt, _settings.AutoPaste, _settings.ClipboardAfterPaste, _settings.Sounds,
            _settings.OpenAiKey, _settings.SpeechEngine, _settings.TextEngine, _settings.OpenAiTranscriptionModel, _settings.OpenAiPromptModel,
            _settings.AutoUpdateCheck, _settings.Templates, _settings.TemplateHotkey, _settings.LastTemplateId,
            _settings.SendScreenshots, _settings.ScreenshotHotkey, _settings.LiveDictateHotkey, _settings.PunctuationMode,
            _settings.GlossaryHotkeyEnabled, _settings.ScreenshotHotkeyEnabled, _settings.LiveDictateHotkeyEnabled,
            _settings.OpenSettingsHotkey, _settings.OpenSettingsHotkeyEnabled, _settings.RightCtrlDictation,
            _settings.TranscriptionLanguage, _settings.TranscriptionTemperature, _settings.TranscriptionThinking, _settings.DictateMode) = before;
            RestoreReadAloud(readAloudBefore);
            RestoreMeetings(meetingsBefore);
            RestoreChat(chatBefore);
            // Said once, then in the status line: this runs after every edit.
            var error = Core.UserCopy.TechnicalFallback("Die Einstellungen konnten nicht gespeichert werden.", e.Message);
            if (error != _lastSaveError) Warn(error);
            _lastSaveError = error;
            _snapshot = Snapshot();
            UpdateSaveState();
            return false;
        }
        _lastSaveError = null;
        if (_autoStart.Checked != AutoStart.IsEnabled)
        {
            if (!(_autoStart.Checked ? AutoStart.Enable() : AutoStart.Disable()))
                Warn("Der automatische Start konnte nicht geändert werden. Details stehen im Protokoll.");
        }
        Core.Log.Info($"Settings saved (autostart {AutoStart.IsEnabled}, offline {_settings.OfflineMode}, llm {_settings.OfflineLlmId}, key set: {_settings.ApiKey.Length > 0}, mic {(_settings.Microphone.Length > 0 ? _settings.Microphone : "default")}, glossary {_settings.Glossary.Count}, quick actions {_settings.QuickActions.Count}, template {_settings.DictatePromptTemplate.Length} chars, models {_settings.TranscriptionModel}/{_settings.PromptModel}, own prompts {_settings.DictateSystemPrompt.Length}/{_settings.PromptModeSystemPrompt.Length} chars, auto-paste {_settings.AutoPaste}, after paste {_settings.ClipboardAfterPaste}, sounds {_settings.Sounds}, update check {_settings.AutoUpdateCheck}, engines {_settings.SpeechEngine}/{_settings.TextEngine}, openai key set: {_settings.OpenAiKey.Length > 0}, openai models {_settings.OpenAiTranscriptionModel}/{_settings.OpenAiPromptModel}, templates {_settings.Templates.Count}, template key {(_settings.TemplateHotkey.Length > 0 ? "set" : "none")}, screenshots {_settings.SendScreenshots}, screenshot key {(_settings.ScreenshotHotkey.Length > 0 ? "set" : "none")}, live key {(_settings.LiveDictateHotkey.Length > 0 ? "set" : "none")}, punctuation {_settings.PunctuationMode}, dictate mode {_settings.DictateMode})");
        // Also with a key problem: the same state is not written again until the user edits something.
        _snapshot = Snapshot();
        _loadedOffline = _settings.OfflineMode;
        if (keysOk) _savedHintUntil = Environment.TickCount64 + 2000;
        UpdateSaveState();
        Saved?.Invoke();
        return true;
    }

    /// <summary>On close: the prompt texts that were replaced go into "Frühere Fassungen".</summary>
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

    // ---------------- Vorlagen ----------------

    private const string TemplateExample = "Das Diktat ist eine ärztliche Behandlungsdokumentation. Schreibe sie mit den Abschnitten "
        + "„Anamnese:“, „Befund:“ und „Therapie:“, jeder Abschnitt in einer neuen Zeile. Ordne alles Gesagte dem passenden Abschnitt zu. "
        + "Übernimm alle Angaben vollständig und genau (Zahlen, Dosierungen, Seitenangaben). Fasse nichts zusammen, lass nichts weg "
        + "und füge nichts hinzu. Wurde zu einem Abschnitt nichts gesagt, lass ihn weg.";

    private sealed record TemplateItem(Core.DictationTemplate Template, int Index)
    {
        public override string ToString() => $"{Index + 1}   {Template.DisplayName}";
    }

    private Core.DictationTemplate? CurrentTemplate =>
        _templateChoice.SelectedItem is TemplateItem item && item.Index < _templates.Count ? _templates[item.Index] : null;

    /// <summary>Refills the list (after add or remove) and shows template <paramref name="select"/>.</summary>
    private void FillTemplateChoice(int select)
    {
        _loadingTemplate = true;
        _templateChoice.BeginUpdate();
        _templateChoice.Items.Clear();
        for (int i = 0; i < _templates.Count; i++) _templateChoice.Items.Add(new TemplateItem(_templates[i], i));
        _templateChoice.SelectedIndex = _templates.Count == 0 ? -1 : Math.Clamp(select, 0, _templates.Count - 1);
        _templateChoice.EndUpdate();
        _loadingTemplate = false;
        LoadTemplate();
    }

    /// <summary>Shows the selected template in the editor; with none, only the "Neue Vorlage" hint.</summary>
    private void LoadTemplate()
    {
        var t = CurrentTemplate;
        bool any = t != null;
        _templateEditor.Visible = any;
        _templateEmpty.Visible = !any;
        _templateChoice.Enabled = any;
        _templateRemove.Enabled = any;
        _loadingTemplate = true;
        _templateName.Text = t?.Name ?? "";
        _templateInstructions.Text = (t?.Instructions ?? "").ReplaceLineEndings("\r\n");
        _templateOwnKey.SetValue(t?.Hotkey ?? "");
        _loadingTemplate = false;
        UpdateTemplateState();
    }

    /// <summary>A field of the selected template changed: keep it in the working copy, refresh the list entry.</summary>
    private void TemplateEdited()
    {
        if (_loadingTemplate || CurrentTemplate is not { } t) return;
        bool renamed = t.Name != _templateName.Text;
        t.Name = _templateName.Text;
        t.Instructions = _templateInstructions.Text;
        t.Hotkey = _templateOwnKey.Value;
        if (renamed)
        {
            // WHY replace the item: a combo box draws the text it read when the item was added.
            int index = _templateChoice.SelectedIndex;
            _loadingTemplate = true;
            _templateChoice.Items[index] = new TemplateItem(t, index);
            _templateChoice.SelectedIndex = index;
            _loadingTemplate = false;
        }
        UpdateTemplateState();
    }

    /// <summary>One line under the format box: what is missing, or which key starts this template.</summary>
    private void UpdateTemplateState()
    {
        var t = CurrentTemplate;
        bool hasPicker = Hotkey.TryParse(_templateKey.Value, out var picker);
        _templateState.Text = t == null ? ""
            : !t.IsUsable ? "Noch ohne Anweisungen: beschreiben, wie der Text aussehen soll, damit die Vorlage etwas tut."
            : Hotkey.TryParse(t.Hotkey, out var own)
                ? $"Startet mit {own.ToDisplayString()}" + (hasPicker ? $", als zuletzt benutzte auch mit {picker.ToDisplayString()}" : "")
            : hasPicker ? $"Startet als zuletzt benutzte Vorlage mit {picker.ToDisplayString()}"
            : "Noch ohne Kürzel: oben das Kürzel für Vorlagen festlegen oder unten ein eigenes.";
    }

    private void AddTemplate()
    {
        _templates.Add(new Core.DictationTemplate());
        FillTemplateChoice(_templates.Count - 1);
        _templateName.Focus();
    }

    private void RemoveTemplate()
    {
        if (CurrentTemplate is not { } t) return;
        if (ChoiceDialog.Ask(this, "Vorlage löschen?", $"„{t.DisplayName}“ wird gelöscht.",
                new[] { "Löschen", "Abbrechen" }, danger: true) != 0) return;
        int index = _templateChoice.SelectedIndex;
        _templates.RemoveAt(index);
        FillTemplateChoice(index);
    }

    private void InsertTemplateExample()
    {
        if (CurrentTemplate == null) return;
        if (_templateInstructions.Text.Trim().Length > 0 && _templateInstructions.Text.Trim() != TemplateExample
            && ChoiceDialog.Ask(this, "Anweisungen ersetzen?", "Die jetzigen Anweisungen dieser Vorlage werden ersetzt.",
                new[] { "Ersetzen", "Abbrechen" }) != 0) return;
        _templateInstructions.Text = TemplateExample;
        if (_templateName.Text.Trim().Length == 0) _templateName.Text = "Behandlungsnotiz";
    }

    /// <summary>
    /// The Vorlagen key and each template's own key: valid, and not taken by Diktieren, Text bearbeiten, Glossar,
    /// the Vorlagen key or an earlier template. Same rules and the same kind of warning as the other hotkeys.
    /// </summary>
    /// <param name="app">The parsed app hotkeys in the order Diktieren, Dictate Prompt, Ins Glossar (null = off).</param>
    private string? TemplateKeyProblem(Hotkey?[] app)
    {
        var taken = new List<(string Owner, Hotkey Key)>();
        foreach (var (owner, key) in new[] { "Diktieren", "Dictate Prompt", "Ins Glossar" }.Zip(app))
            if (key is { } k) taken.Add((owner, k));
        if (Hotkey.TryParse(_settingsKey.Value, out var settingsKey)) taken.Add(("Einstellungen öffnen", settingsKey));
        // The Live Meeting key is set from the tray menu, not here, but it is just as taken.
        if (Hotkey.TryParse(_settings.MeetingHotkey, out var meetingKey)) taken.Add(("Live Meeting", meetingKey));
        const string Rule = "Bitte ein Tastenkürzel mit zwei Zusatztasten festlegen, z. B. Strg+Umschalt+V, oder es mit Entf entfernen. "
            + "Strg+Alt allein geht nicht, das ist auf deutschen Tastaturen AltGr.";
        if (_liveKey.Value.Length > 0)
        {
            if (!Hotkey.TryParse(_liveKey.Value, out var live) || !IsSafe(live))
            return "Kürzel für Live diktieren: " + Rule;
            if (taken.FirstOrDefault(k => k.Key == live) is { Owner: { } owner })
            return $"{live.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für Live diktieren ein anderes Kürzel wählen.";
            taken.Add(("Live diktieren", live));
        }
        if (_screenshotKey.Value.Length > 0)
        {
            if (!Hotkey.TryParse(_screenshotKey.Value, out var shot) || !IsSafe(shot))
            return "Kürzel für Bildschirmfoto: " + Rule;
            if (taken.FirstOrDefault(k => k.Key == shot) is { Owner: { } owner })
            return $"{shot.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für das Bildschirmfoto ein anderes Kürzel wählen.";
            taken.Add(("Bildschirmfoto", shot));
        }
        if (ReadAloudKeyProblem(taken, Rule) is { } readAloudProblem) return readAloudProblem;
        if (ChatKeyProblem(taken, Rule) is { } chatProblem) return chatProblem;
        if (_templateKey.Value.Length > 0)
        {
            if (!Hotkey.TryParse(_templateKey.Value, out var picker) || !IsSafe(picker))
            return "Kürzel für Vorlagen: " + Rule;
            if (taken.FirstOrDefault(k => k.Key == picker) is { Owner: { } owner })
            return $"{picker.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für Vorlagen ein anderes Kürzel wählen.";
            taken.Add(("Vorlagen (zuletzt benutzte)", picker));
        }
        for (int i = 0; i < _templates.Count; i++)
        {
            var t = _templates[i];
            if (t.Hotkey.Length == 0) continue;
            if (!Hotkey.TryParse(t.Hotkey, out var key) || !IsSafe(key))
            return $"Vorlage „{t.DisplayName}“: " + Rule;
            if (taken.FirstOrDefault(k => k.Key == key) is { Owner: { } owner })
            return $"{key.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für die Vorlage „{t.DisplayName}“ ein anderes Kürzel wählen.";
            taken.Add(($"Vorlage „{t.DisplayName}“", key));
        }
        return null;
    }

    /// <summary>
    /// A field that may be empty, to the stored key and its on flag (Mac isEnabled): a combination switches it on; empty
    /// switches it off and keeps the previous key as the suggestion.
    /// </summary>
    private static (string Hotkey, bool Enabled) StoredKey(string boxValue, string current) =>
        Hotkey.TryParse(boxValue, out var key) ? (key.ToString(), true) : (current, false);

    private void ShowTemplate(int index)
    {
        ShowPage(TemplatesPage);
        if (_templateChoice.SelectedIndex != index) FillTemplateChoice(index);
        _templateOwnKey.Focus();
    }

    /// <summary>The glossary hotkey added a term while this window is open: show it, so the next apply keeps it.</summary>
    public void AppendGlossaryTerm(string term)
    {
        var lines = _glossary.Lines.ToList();
        if (lines.Any(l => string.Equals(l.Trim(), term, StringComparison.CurrentCultureIgnoreCase))) return;
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(term);
        _glossary.Text = string.Join("\r\n", lines);
    }

    /// <summary>
    /// Two of Ctrl/Alt/Shift, one of them Ctrl or Alt, or Alt+digit (Mac digits). Ctrl+V or Alt+F as a
    /// global hotkey would take that key away from every program. Ctrl+Alt without Shift is AltGr on
    /// German keyboards (Ctrl+Alt+Q would swallow every @). Rule: <see cref="Core.HotkeyText.IsSafe"/>.
    /// </summary>
    private static bool IsSafe(Hotkey hotkey) => hotkey.IsSafe;

    private void Warn(string text) =>
        MessageBox.Show(this, text, "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void OpenWeb(string url)
    {
        if (!Core.NetworkPolicy.IsAllowedBrowserUrl(url))
        {
            Core.Log.Error("Opening URL refused: not HTTPS");
            return;
        }
        OpenLocal(url);
    }

    private static void OpenLocal(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening URL failed: {e.GetType().Name}"); }
    }

    /// <summary>Text box that records a key combination instead of text.</summary>
    private sealed class HotkeyBox : TextBox
    {
        private readonly bool _allowEmpty;

        public string Value { get; private set; }

        /// <summary>The user recorded or removed a combination (not raised by <see cref="SetValue"/>).</summary>
        public event Action? ValueChanged;

        /// <param name="allowEmpty">Optional key (Vorlagen): Entf or Backspace removes it, the field then reads „Kein Kürzel“.</param>
        public HotkeyBox(string stored, bool allowEmpty = false)
        {
            _allowEmpty = allowEmpty;
            Value = stored;
            Text = Hotkey.Display(stored);
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            if (allowEmpty) PlaceholderText = "Kein Kürzel";
        }

        /// <summary>Shows another stored combination ("" = none), e.g. when a different template is selected.</summary>
        public void SetValue(string stored)
        {
            Value = stored;
            Text = Hotkey.Display(stored);
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
            // Ctrl+Tab / Ctrl+PageDown belong to the settings pages, not to a new hotkey.
            if (e.Modifiers.HasFlag(Keys.Control) && e.KeyCode is Keys.Tab or Keys.Next or Keys.PageUp)
            {
                e.SuppressKeyPress = false;
                e.Handled = false;
                return;
            }
            if (_allowEmpty && e.KeyCode is Keys.Delete or Keys.Back && e.Modifiers == Keys.None)
            {
                Value = "";
                Text = "";
                ValueChanged?.Invoke();
                return;
            }
            Apply(e.KeyCode, e.Modifiers);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            // Released without a main key: show the stored combination again.
            if (Text.EndsWith('+')) Text = Hotkey.Display(Value);
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
            ValueChanged?.Invoke();
        }
    }
}
