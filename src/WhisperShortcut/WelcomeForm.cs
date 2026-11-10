using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace WhisperShortcut;

/// <summary>
/// The welcome tour, like the Mac's (Onboarding/WelcomeView.swift, WelcomeSteps.swift): Intro, Einrichtung
/// (Offline or Cloud), Mikrofon, Ausprobieren, Fertig, with Back/Next and step dots in the footer. It uses the
/// settings window's look (SettingsUi: cards, buttons, theme). Closing the window is not finishing: the step is stored
/// and the next start resumes there. „Fertig“ marks the tour completed and raises <see cref="Completed"/>.
/// </summary>
internal sealed class WelcomeForm : FastShowForm
{
    private const int ContentWidth = 680, PagePadX = 40;

    private readonly Core.Settings _settings;
    private readonly OfflineEngine _offline;
    private readonly Panel _host;
    private readonly TableLayoutPanel[] _pages;
    private readonly FluentButton _back, _next;
    private readonly StepDots _dots;
    private readonly ThemedLabel _index;
    private readonly List<(Label Label, int Inset)> _wraps = new();
    private Core.WelcomeStep _step;

    // Einrichtung
    private readonly ChoiceCard _offlineCard, _cloudCard;
    private readonly Control _offlineDetails, _cloudDetails;
    private readonly FluentButton _speechLoad;
    private readonly ThemedLabel _speechStatus;
    private readonly Bar _speechProgress;
    private readonly ToggleSwitch _offlineMode;
    private readonly SettingsForm.KeyField _googleKey, _openAiKey;
    private readonly Control _openAiBlock;
    private readonly LinkLabel _otherProviders;
    private readonly ThemedLabel _setupStatus;
    private Core.DictationChoice? _choice;

    // Mikrofon
    private readonly Bar _micMeter;
    private readonly ThemedLabel _micStatus;
    private readonly FluentButton _micTest;
    private readonly System.Windows.Forms.Timer _micTimer = new() { Interval = 50 };
    private LevelProbe? _probe;
    private long _probeStarted;
    private float _probePeak;

    // Ausprobieren
    private readonly ThemedLabel _tryStatus;
    private readonly TextBox _tryText;
    private readonly ThemedLabel _trySubtitle;

    // Fertig
    private readonly ThemedLabel _doneTitle, _doneHint;
    private readonly TableLayoutPanel _shortcutList;
    private readonly Card _shortcutCard;

    /// <summary>The user pressed „Fertig“; the settings are already saved with the tour completed.</summary>
    public event Action? Completed;

    /// <summary>A setting changed in the tour (key, offline model choice, offline mode) and was saved.</summary>
    public event Action? Saved;

    public WelcomeForm(Core.Settings settings, OfflineEngine offline, Core.GeminiClient gemini, Core.OpenAiClient openAi)
    {
        _settings = settings;
        _offline = offline;
        _step = Core.WelcomeTour.Resume(settings.WelcomeStep);
        _choice = Core.WelcomeTour.Inferred(settings, OfflineEngine.SpeechInstalled);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Einführung";
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(ContentWidth + 2 * PagePadX + 20, 640); // Mac: min 720×640
        MinimumSize = new Size(640, 560);
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        Icon = TrayIcons.App;
        KeyPreview = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(PagePadX, 28, PagePadX, 12), Margin = new Padding(0) };
        root.Controls.Add(_host, 0, 0);
        root.Controls.Add(new Line(), 0, 1);

        // ---- footer: dots and "n von 5" in the middle, Zurück / Weiter on the right (Mac footerBar) ----
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Padding = new Padding(PagePadX, 16, PagePadX, 20), Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var dotsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        _dots = new StepDots { Margin = new Padding(0, 10, 0, 0) };
        _index = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(8, 7, 0, 0) };
        dotsRow.Controls.Add(_dots);
        dotsRow.Controls.Add(_index);
        footer.Controls.Add(dotsRow, 0, 0);
        _back = new FluentButton("Zurück") { MinimumSize = new Size(110, 32) };
        _next = new FluentButton("Weiter", accent: true) { MinimumSize = new Size(110, 32) };
        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => Advance();
        footer.Controls.Add(_back, 2, 0);
        footer.Controls.Add(_next, 3, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        AcceptButton = _next;

        // ================= Intro (Mac WelcomeIntroStep) =================
        var intro = NewPage();
        intro.Padding = new Padding(0, 60, 0, 0);
        intro.Controls.Add(Centered(new ThemedLabel { Text = "", Font = new Font(Theme.Icons.FontFamily, 40f), Tone = Tone.Accent, AutoSize = true }));
        intro.Controls.Add(Centered(new ThemedLabel { Text = "Willkommen bei WhisperShortcut", Font = Theme.Title, AutoSize = true, Margin = new Padding(0, 20, 0, 6) }));
        intro.Controls.Add(Centered(new ThemedLabel { Text = "Aus deiner Stimme wird Text, in jedem Programm.", Font = Theme.Section, Tone = Tone.Secondary, AutoSize = true }));
        var introText = Wrap("Drei kurze Schritte: offline oder in der Cloud diktieren, das Mikrofon prüfen und einmal ausprobieren. Etwa eine Minute.", 80);
        introText.TextAlign = ContentAlignment.MiddleCenter;
        intro.Controls.Add(Centered(introText, top: 22));

        // ================= Einrichtung (Mac WelcomeDictationSetupStep) =================
        var setup = NewPage();
        setup.Controls.Add(Header("", "Wie soll das Diktat laufen?",
            "Wähl eins für den Anfang. Das andere kannst du jederzeit in den Einstellungen ergänzen."));
        long parakeetSize = Core.OfflineCatalog.Parakeet.Files.Sum(f => f.Size);
        _offlineCard = new ChoiceCard("", "Offline", "Privat und kostenlos", new[]
        {
            "Die Aufnahme verlässt diesen PC nie",
            "Funktioniert ohne Internet und ohne Konto",
            $"Einmal laden, {FormatBytes(parakeetSize)}",
        });
        _cloudCard = new ChoiceCard("", "Cloud", "Am genauesten", new[]
        {
            "Beste Erkennung, in mehr Sprachen",
            "Ermöglicht auch Chat und Vorlesen",
            "Braucht ein Konto, du bezahlst den Anbieter",
        });
        var cards = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 18, 0, 12) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _offlineCard.Margin = new Padding(0, 0, 6, 0);
        _cloudCard.Margin = new Padding(6, 0, 0, 0);
        cards.Controls.Add(_offlineCard, 0, 0);
        cards.Controls.Add(_cloudCard, 1, 0);
        setup.Controls.Add(cards);
        _offlineCard.Picked += () => Choose(Core.DictationChoice.Offline);
        _cloudCard.Picked += () => Choose(Core.DictationChoice.Cloud);

        // Offline: load Parakeet, and the switch that keeps everything on this PC.
        var offlineCard = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var speechBlock = Block();
        var speechRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
        speechRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        speechRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var speechText = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        speechText.Controls.Add(new ThemedLabel { Text = "Parakeet – Spracherkennung auf diesem PC", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0) });
        speechText.Controls.Add(Wrap("Versteht Deutsch, Englisch und weitere Sprachen.", 2 * 16 + 140, small: true));
        _speechStatus = new ThemedLabel { AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 4, 0, 0) };
        speechText.Controls.Add(_speechStatus);
        _speechLoad = new FluentButton("Laden") { MinimumSize = new Size(120, 32), Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(16, 2, 0, 0) };
        _speechLoad.Click += (_, _) => ToggleSpeechDownload();
        speechRow.Controls.Add(speechText, 0, 0);
        speechRow.Controls.Add(_speechLoad, 1, 0);
        speechBlock.Controls.Add(speechRow);
        _speechProgress = new Bar(level: false) { Margin = new Padding(0, 10, 0, 0), Visible = false };
        speechBlock.Controls.Add(_speechProgress);
        offlineCard.AddRow(speechBlock);
        var modeBlock = Block();
        _offlineMode = new ToggleSwitch { Checked = settings.OfflineMode, AccessibleName = "Alle Cloud-Funktionen sperren (Offline-Modus)" };
        modeBlock.Controls.Add(Row(new ThemedLabel { Text = "Alle Cloud-Funktionen sperren (Offline-Modus)", AutoSize = true, Anchor = AnchorStyles.Left }, _offlineMode));
        modeBlock.Controls.Add(Wrap("Für Patientendaten: Nichts verlässt diesen PC. Dictate Prompt braucht dann zusätzlich das Sprachmodell "
            + "(Einstellungen → Dictate Prompt). Später unter Einstellungen → Datenschutz & Berechtigungen änderbar.", 2 * 16, small: true));
        _offlineMode.CheckedChanged += (_, _) => SetOfflineMode(_offlineMode.Checked);
        offlineCard.AddRow(modeBlock);
        _offlineDetails = offlineCard;
        setup.Controls.Add(offlineCard);

        // Cloud: the Google key first, OpenAI folded away (Mac: OpenRouter, Google; others in a disclosure).
        var cloudCard = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _googleKey = new SettingsForm.KeyField(settings.ApiKey, (k, ct) => gemini.CheckKeyAsync(k, ct), SettingsForm.GeminiKeyMessage,
            k => SaveKey(k, gemini: true));
        cloudCard.AddRow(KeyBlock(_googleKey, "Google-API-Schlüssel (Gemini)",
            "Du hast schon einen Schlüssel aus Google AI Studio? Hier einfügen. Am besten aus einem Projekt mit Abrechnung: "
            + "Im kostenlosen Kontingent darf Google die Aufnahmen zum Training nutzen.",
            "Schlüssel in Google AI Studio erstellen", "https://aistudio.google.com/apikey"));
        _openAiKey = new SettingsForm.KeyField(settings.OpenAiKey, (k, ct) => openAi.CheckKeyAsync(k, ct), SettingsForm.OpenAiKeyMessage,
            k => SaveKey(k, gemini: false));
        var otherBlock = Block();
        _otherProviders = new LinkLabel { Text = "▸ Weitere Anbieter – OpenAI", AutoSize = true, Margin = new Padding(0) };
        otherBlock.Controls.Add(_otherProviders);
        cloudCard.AddRow(otherBlock);
        _openAiBlock = KeyBlock(_openAiKey, "OpenAI-API-Schlüssel",
            "Ein API-Schlüssel mit Guthaben auf platform.openai.com; ein ChatGPT-Abo zählt dafür nicht.",
            "Schlüssel bei OpenAI erstellen", "https://platform.openai.com/api-keys");
        _openAiBlock.Visible = settings.OpenAiKey.Length > 0;
        cloudCard.AddRow(_openAiBlock);
        _otherProviders.LinkClicked += (_, _) =>
        {
            _openAiBlock.Visible = !_openAiBlock.Visible;
            UpdateOtherProvidersLink();
        };
        UpdateOtherProvidersLink();
        _cloudDetails = cloudCard;
        setup.Controls.Add(cloudCard);

        setup.Controls.Add(Wrap("Deine Aufnahmen gehen nur dorthin, wo du es hier festlegst, nie an uns. Schlüssel bleiben verschlüsselt auf diesem PC.",
            0, small: true, top: 12));
        _setupStatus = new ThemedLabel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        setup.Controls.Add(_setupStatus);
        _offline.DownloadChanged += OnDownloadChanged;

        // ================= Mikrofon (Mac WelcomePermissionsStep; Windows asks nothing up front, so test it) =================
        var mic = NewPage();
        mic.Controls.Add(Header("", "Mikrofon",
            "Nur das Mikrofon ist nötig. Sprich ein paar Worte: Der Balken zeigt, ob WhisperShortcut dich hört."));
        var micCard = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 18, 0, 4) };
        var micBlock = Block();
        string device = settings.Microphone.Length > 0 ? settings.Microphone : "Windows-Standard";
        _micTest = new FluentButton("Testen") { MinimumSize = new Size(100, 32) };
        _micTest.Click += (_, _) => { if (_probe == null) StartMicTest(); else StopMicTest(); };
        micBlock.Controls.Add(Row(new ThemedLabel { Text = $"Mikrofon: {device}", AutoSize = true, Anchor = AnchorStyles.Left }, _micTest));
        _micMeter = new Bar(level: true) { Margin = new Padding(0, 10, 0, 4) };
        micBlock.Controls.Add(_micMeter);
        _micStatus = new ThemedLabel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        micBlock.Controls.Add(_micStatus);
        micCard.AddRow(micBlock);
        var privacyBlock = Block();
        var privacyButton = new FluentButton("Windows-Einstellungen öffnen") { MinimumSize = new Size(120, 32) };
        privacyButton.Click += (_, _) => OpenUri("ms-settings:privacy-microphone");
        privacyBlock.Controls.Add(Row(Wrap("Hört der Balken nichts? Unter Windows → Datenschutz und Sicherheit → Mikrofon müssen „Mikrofonzugriff“ und "
            + "„Desktop-Apps den Zugriff auf das Mikrofon erlauben“ eingeschaltet sein.", 2 * 16 + 260, small: true), privacyButton));
        privacyBlock.Controls.Add(Wrap("Ein anderes Mikrofon wählst du unter Einstellungen → Allgemein → Mikrofon.", 2 * 16, small: true, top: 6));
        micCard.AddRow(privacyBlock);
        mic.Controls.Add(micCard);
        _micTimer.Tick += (_, _) => UpdateMicTest();

        // ================= Ausprobieren (Mac WelcomeTryItStep, with the real shortcut instead of a button) =================
        var tryIt = NewPage();
        var tryHeader = Header("", "Jetzt ausprobieren", "");
        _trySubtitle = (ThemedLabel)tryHeader.Tag!;
        tryIt.Controls.Add(tryHeader);
        var tryCard = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 18, 0, 4) };
        var tryBlock = Block();
        _tryStatus = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Margin = new Padding(0, 0, 0, 8) };
        tryBlock.Controls.Add(_tryStatus);
        _tryText = new TextBox { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Height = 120, AccessibleName = "Dein Diktat" };
        tryBlock.Controls.Add(new FieldFrame(_tryText, multiline: true) { Height = 130 });
        tryCard.AddRow(tryBlock);
        tryIt.Controls.Add(tryCard);
        tryIt.Controls.Add(Wrap("Du kannst diesen Schritt auch überspringen.", 0, small: true, top: 8));
        ResetTry();

        // ================= Fertig (Mac WelcomeDoneStep) =================
        var done = NewPage();
        done.Padding = new Padding(0, 16, 0, 0);
        _doneTitle = new ThemedLabel { Font = Theme.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        done.Controls.Add(Centered(_doneTitle));
        _doneHint = Wrap("", 80);
        _doneHint.TextAlign = ContentAlignment.MiddleCenter;
        done.Controls.Add(Centered(_doneHint));
        // Windows hides new tray icons behind the arrow; the taskbar page is where "always show" is switched on.
        var pinButton = new FluentButton("Symbol immer anzeigen …") { MinimumSize = new Size(120, 32), Margin = new Padding(0, 8, 0, 0) };
        pinButton.Click += (_, _) => OpenUri("ms-settings:taskbar");
        done.Controls.Add(Centered(pinButton));
        done.Controls.Add(Wrap("Deine Tastenkürzel:", 0, small: true, top: 18));
        _shortcutCard = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 0, 4) };
        _shortcutList = Block();
        _shortcutCard.AddRow(_shortcutList);
        done.Controls.Add(_shortcutCard);
        done.Controls.Add(Wrap("Eine andere Kombination nimmst du unter Einstellungen → Allgemein, falls eine mit einem deiner Programme kollidiert.",
            0, small: true, top: 6));
        var revisit = Wrap(Core.WelcomeTour.RevisitHint, 80, small: true, top: 14);
        revisit.TextAlign = ContentAlignment.MiddleCenter;
        done.Controls.Add(Centered(revisit));

        _pages = new[] { intro, setup, mic, tryIt, done };
        foreach (var page in _pages)
        {
            page.Visible = false;
            _host.Controls.Add(page);
        }
        ResumeLayout(true);

        Load += (_, _) =>
        {
            Theme.Apply(this);
            FitWraps();
            ShowStep(_step);
        };
        _host.Resize += (_, _) => FitWraps();
        HandleCreated += (_, _) => Theme.TitleBar(this);
        FormClosed += (_, _) =>
        {
            StopMicTest();
            _offline.DownloadChanged -= OnDownloadChanged;
            _googleKey.Dispose();
            _openAiKey.Dispose();
            _micTimer.Dispose();
        };
    }

    // ---------------- navigation ----------------

    private bool CanAdvance => Core.WelcomeTour.CanAdvance(_step, TranscriptionReady);

    private bool TranscriptionReady => Core.WelcomeTour.TranscriptionReady(_settings, OfflineEngine.SpeechInstalled);

    /// <summary>The tour is on „Ausprobieren“: a finished dictation belongs here, not into a window.</summary>
    public bool TakesTryResult => !IsDisposed && Visible && _step == Core.WelcomeStep.TryIt;

    /// <summary>Brings the tour to the front.</summary>
    public void Reveal()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ShowStep(Core.WelcomeStep step)
    {
        if (step != Core.WelcomeStep.Permissions) StopMicTest();
        _step = step;
        // Batched: a page becoming visible otherwise lays out each of its nested auto-size tables (see FastShowForm).
        LayoutBatch.Run(_host, () => { for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == (int)step; });
        _host.AutoScrollPosition = Point.Empty;
        _dots.Current = (int)step;
        _index.Text = Core.WelcomeTour.IndexLabel(step);
        _back.Visible = step != Core.WelcomeStep.Intro;
        _next.Text = step == Core.WelcomeStep.Done ? "Fertig" : "Weiter";
        switch (step)
        {
            case Core.WelcomeStep.Setup: UpdateSetup(); break;
            case Core.WelcomeStep.Permissions: StartMicTest(); break;
            case Core.WelcomeStep.TryIt: DescribeTry(); break;
            case Core.WelcomeStep.Done: FillDone(); break;
        }
        UpdateNext();
        // Closing is not finishing: the next start resumes on this step (Mac onboardingCurrentStep).
        _settings.WelcomeStep = (int)step;
        TrySaveSettings();
        Core.Log.Info($"Welcome: step {step}");
    }

    private void UpdateNext() => _next.Enabled = CanAdvance;

    private void Advance()
    {
        if (!CanAdvance) return;
        if (_step == Core.WelcomeStep.Done)
        {
            _settings.WelcomeCompleted = true;
            _settings.WelcomeStep = 0;
            TrySaveSettings();
            Core.Log.Info("Welcome: completed");
            Completed?.Invoke();
            Close();
            return;
        }
        ShowStep(_step + 1);
    }

    private void GoBack()
    {
        if (_step > Core.WelcomeStep.Intro) ShowStep(_step - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Left/right arrows step through the tour, except while a text field has the cursor (Mac arrow-key monitor).
        if (ActiveControl is not TextBox)
        {
            if (keyData == Keys.Left && _step > Core.WelcomeStep.Intro) { GoBack(); return true; }
            if (keyData == Keys.Right && _step < Core.WelcomeStep.Done && CanAdvance) { Advance(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception e) { Core.Log.Error($"Welcome: saving settings failed: {e.GetType().Name}"); }
    }

    // ---------------- Einrichtung ----------------

    private void Choose(Core.DictationChoice choice)
    {
        _choice = choice;
        if (choice == Core.DictationChoice.Offline) Core.WelcomeTour.ChooseOffline(_settings);
        else Core.WelcomeTour.ChooseCloud(_settings);
        if (choice == Core.DictationChoice.Cloud && _offlineMode.Checked) _offlineMode.Checked = false; // saves via SetOfflineMode
        TrySaveSettings();
        Saved?.Invoke();
        Core.Log.Info($"Welcome: chose {choice}");
        UpdateSetup();
        if (choice == Core.DictationChoice.Offline) StartSpeechDownloadIfNeeded();
        if (choice == Core.DictationChoice.Cloud && _googleKey.Value.Length == 0) _googleKey.Box.Focus();
    }

    // Mac #418: picking Offline starts the one-time model download, so Weiter does not wait for a click on Laden that is easy to miss.
    // Only when nothing is installed or running: never a second download, and picking Cloud again never cancels one.
    private void StartSpeechDownloadIfNeeded()
    {
        if (OfflineEngine.SpeechInstalled || _offline.CurrentDownload != null) return;
        ToggleSpeechDownload(); // nothing runs here, so the toggle starts the download
        UpdateSpeechRow();
        Core.Log.Info("Welcome: offline chosen, speech model download started");
    }

    private void UpdateSetup()
    {
        _offlineCard.Selected = _choice == Core.DictationChoice.Offline;
        _cloudCard.Selected = _choice == Core.DictationChoice.Cloud;
        LayoutBatch.Run(_host, () =>
        {
            _offlineDetails.Visible = _choice == Core.DictationChoice.Offline;
            _cloudDetails.Visible = _choice == Core.DictationChoice.Cloud;
        });
        UpdateSpeechRow();
        bool ready = TranscriptionReady;
        _setupStatus.Text = ready ? "✓ Bereit zum Weitermachen." : _choice switch
        {
            Core.DictationChoice.Offline => "Lade die Spracherkennung, dann geht es weiter.",
            Core.DictationChoice.Cloud => "Trag einen Schlüssel ein, dann geht es weiter.",
            _ => "Wähl Offline oder Cloud.",
        };
        _setupStatus.Tone = ready ? Tone.Good : Tone.Secondary;
        UpdateNext();
    }

    private void OnDownloadChanged()
    {
        if (IsDisposed) return;
        UpdateSpeechRow();
        if (_step == Core.WelcomeStep.Setup) UpdateSetup();
    }

    private void UpdateSpeechRow()
    {
        var download = _offline.CurrentDownload;
        bool mine = download?.Id == "speech";
        bool installed = OfflineEngine.SpeechInstalled;
        long size = Core.OfflineCatalog.Parakeet.Files.Sum(f => f.Size);
        _speechProgress.Visible = mine;
        _speechLoad.Visible = !installed || mine;
        _speechLoad.Enabled = download == null || mine;
        _speechLoad.Text = mine ? "Abbrechen" : "Laden";
        string? error = download == null && _offline.LastFinished is { Error: { } e } last && last.Id == "speech" ? e : null;
        if (mine)
        {
            int percent = (int)Math.Round(download!.Fraction * 100);
            _speechProgress.Value = Math.Clamp(percent, 0, 100) / 100f;
            SetStatus(_speechStatus, $"Wird geladen … {percent} % von {FormatBytes(size)}", Tone.Secondary);
        }
        else if (installed) SetStatus(_speechStatus, $"✓ Geladen ({FormatBytes(size)} auf diesem PC)", Tone.Good);
        else if (error != null) SetStatus(_speechStatus, error, Tone.Bad);
        else if (download != null) SetStatus(_speechStatus, "Ein anderes Modell wird gerade geladen. Danach geht es hier weiter.", Tone.Secondary);
        else SetStatus(_speechStatus, $"Noch nicht geladen, ca. {FormatBytes(size)}", Tone.Secondary);
    }

    private void ToggleSpeechDownload()
    {
        var running = _offline.CurrentDownload;
        if (running != null)
        {
            if (running.Id == "speech") running.Cancel.Cancel();
            return;
        }
        _offline.StartDownload("speech", new[] { Core.OfflineCatalog.Parakeet });
    }

    private void SetOfflineMode(bool on)
    {
        if (_settings.OfflineMode == on) return;
        _settings.OfflineMode = on;
        if (on) Core.WelcomeTour.ChooseOffline(_settings);
        TrySaveSettings();
        Saved?.Invoke();
        Core.Log.Info($"Welcome: offline mode {(on ? "on" : "off")}");
        UpdateSetup();
    }

    /// <summary>Saves a key the check confirmed (same rule as the settings window); true when it was new.</summary>
    private bool SaveKey(string key, bool gemini)
    {
        var before = gemini ? _settings.ApiKey : _settings.OpenAiKey;
        if (key == before) return false;
        if (gemini) _settings.ApiKey = key; else _settings.OpenAiKey = key;
        Core.WelcomeTour.ChooseCloud(_settings);
        try
        {
            _settings.Save();
        }
        catch (Exception e)
        {
            Core.Log.Error($"Welcome: saving the checked key failed: {e.GetType().Name}");
            if (gemini) _settings.ApiKey = before; else _settings.OpenAiKey = before;
            return false;
        }
        Core.Log.Info($"Welcome: {(gemini ? "Gemini" : "OpenAI")} key saved after a successful check");
        Saved?.Invoke();
        if (_offlineMode.Checked) _offlineMode.Checked = false;
        UpdateSetup();
        return true;
    }

    private void UpdateOtherProvidersLink() =>
        _otherProviders.Text = (_openAiBlock.Visible ? "▾" : "▸") + " Weitere Anbieter – OpenAI";

    private TableLayoutPanel KeyBlock(SettingsForm.KeyField key, string title, string note, string linkText, string url)
    {
        var block = Block();
        key.Box.AccessibleName = title;
        block.Controls.Add(new ThemedLabel { Text = title, Font = Theme.Strong, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        var show = new FluentButton("Anzeigen") { MinimumSize = new Size(100, 32), AccessibleName = title + " anzeigen oder verbergen" };
        show.Click += (_, _) =>
        {
            key.Box.UseSystemPasswordChar = !key.Box.UseSystemPasswordChar;
            show.Text = key.Box.UseSystemPasswordChar ? "Anzeigen" : "Verbergen";
        };
        var check = new FluentButton("Prüfen") { MinimumSize = new Size(100, 32), AccessibleName = title + " prüfen" };
        check.Click += (_, _) => _ = key.CheckAsync();
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var frame = new FieldFrame(key.Box) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.None };
        row.Controls.Add(frame, 0, 0);
        row.Controls.Add(show, 1, 0);
        row.Controls.Add(check, 2, 0);
        block.Controls.Add(row);
        key.Status.MaximumSize = new Size(ContentWidth - 2 * 16, 0);
        block.Controls.Add(key.Status);
        block.Controls.Add(Wrap(note, 2 * 16, small: true, top: 4));
        var link = new LinkLabel { Text = linkText, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        link.LinkClicked += (_, _) => OpenUri(url);
        block.Controls.Add(link);
        return block;
    }

    // ---------------- Mikrofon ----------------

    private void StartMicTest()
    {
        if (_probe != null) return;
        var probe = new LevelProbe(_settings.Microphone);
        try
        {
            probe.Start();
        }
        catch (Exception e)
        {
            probe.Dispose();
            Core.Log.Error($"Welcome: microphone test failed: {e.GetType().Name}");
            SetStatus(_micStatus, Core.UserCopy.TechnicalFallback("Das Mikrofon lässt sich nicht öffnen.", e.Message), Tone.Bad);
            return;
        }
        _probe = probe;
        _probeStarted = Environment.TickCount64;
        _probePeak = 0;
        _micTest.Text = "Beenden";
        SetStatus(_micStatus, "Sprich ein paar Worte …", Tone.Secondary);
        _micTimer.Start();
    }

    private void StopMicTest()
    {
        _micTimer.Stop();
        _probe?.Dispose();
        _probe = null;
        if (!_micTest.IsDisposed) _micTest.Text = "Testen";
        if (!_micMeter.IsDisposed) _micMeter.Value = 0;
    }

    private void UpdateMicTest()
    {
        if (_probe == null) return;
        float level = _probe.Level;
        _micMeter.Value = level;
        _probePeak = Math.Max(_probePeak, level);
        long ms = Environment.TickCount64 - _probeStarted;
        // Same bars as the recording overlay: under 1 % after 3 s is a blocked or muted microphone, not a quiet voice.
        if (_probePeak >= 0.05f) SetStatus(_micStatus, "✓ Das Mikrofon hört dich.", Tone.Good);
        else if (Core.LevelMeter.IsSilent(_probePeak, TimeSpan.FromMilliseconds(ms), TimeSpan.FromSeconds(3)))
            SetStatus(_micStatus, "Kein Ton vom Mikrofon. Ist es stumm geschaltet oder unter Windows → Datenschutz gesperrt?", Tone.Caution);
        // Ten seconds are enough; the meter stops so the microphone is not held open while the user reads.
        if (ms > 10000) StopMicTest();
    }

    // ---------------- Ausprobieren ----------------

    private void DescribeTry()
    {
        var key = _settings.ActiveDictateHotkey;
        string how = key.Length > 0 ? Hotkey.Display(key)
            : _settings.RightCtrlDictation ? "die rechte Strg-Taste" : "im Menü am Symbol „Diktieren“";
        string again = key.Length > 0 || _settings.RightCtrlDictation ? "drück sie noch einmal" : "klick noch einmal";
        _trySubtitle.Text = $"Sprich einen Satz: Drück {how}, sprich, dann {again}. Später macht {how} dasselbe in jedem Programm "
            + "und setzt den Text dort ein, wo der Cursor steht.";
        if (!TranscriptionReady)
        {
            SetStatus(_tryStatus, "Noch kein Diktat eingerichtet. Geh einen Schritt zurück zu „Einrichtung“.", Tone.Caution);
        }
        else if (_tryText.Text.Length == 0)
        {
            ResetTry();
        }
    }

    private void ResetTry()
    {
        _tryText.Text = "";
        SetStatus(_tryStatus, "Wartet auf dein Diktat …", Tone.Secondary);
    }

    /// <summary>A dictation started while the tour shows „Ausprobieren“.</summary>
    public void ShowTryListening()
    {
        if (!TakesTryResult) return;
        _tryText.Text = "";
        SetStatus(_tryStatus, "Hört zu … drück das Kürzel noch einmal, wenn du fertig bist.", Tone.Accent);
    }

    /// <summary>The dictation of „Ausprobieren“ is done (Mac OnboardingTryItPanel: „Es funktioniert“).</summary>
    public void ShowTryResult(string text)
    {
        if (IsDisposed) return;
        _tryText.Text = text.Length > 0 ? text.ReplaceLineEndings("\r\n") : "(keine Sprache erkannt)";
        SetStatus(_tryStatus, text.Length > 0 ? "✓ Es funktioniert. Der Text liegt auch in der Zwischenablage." : "Nichts verstanden. Versuch es noch einmal.",
            text.Length > 0 ? Tone.Good : Tone.Caution);
    }

    // ---------------- Fertig ----------------

    private void FillDone()
    {
        bool ready = TranscriptionReady;
        _doneTitle.Text = ready ? "Alles bereit" : "Fast fertig";
        _doneHint.Text = ready
            ? "WhisperShortcut sitzt unten rechts im Infobereich (oft hinter dem Pfeil ^). Ein Rechtsklick auf das Symbol zeigt das Menü."
            : "Fürs Diktat fehlt noch ein Schlüssel oder das Offline-Modell. Nach „Fertig“ öffnen sich die Einstellungen auf „Konten“.";
        _doneHint.Tone = ready ? Tone.Secondary : Tone.Caution;

        _shortcutList.SuspendLayout();
        _shortcutList.Controls.Clear();
        _shortcutList.RowStyles.Clear();
        void Add(string key, string name, string detail)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 4) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new KeyCap(key) { Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);
            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            text.Controls.Add(new ThemedLabel { Text = name, Font = Theme.Strong, AutoSize = true, Margin = new Padding(0) });
            text.Controls.Add(new ThemedLabel { Text = detail, Font = Theme.Small, Tone = Tone.Secondary, AutoSize = true,
                MaximumSize = new Size(ContentWidth - 2 * 16 - 160, 0), Margin = new Padding(0, 2, 0, 0) });
            row.Controls.Add(text, 1, 0);
            _shortcutList.Controls.Add(row);
        }
        static string Show(string key) => Hotkey.Display(key);
        if (_settings.ActiveDictateHotkey.Length > 0)
            Add(Show(_settings.ActiveDictateHotkey), "Diktieren", "drücken und lossprechen, deine Worte werden zu Text");
        if (_settings.RightCtrlDictation)
            Add("Rechte Strg", "Diktieren", "einmal drücken startet, noch einmal beendet das Diktat");
        if (_settings.ActivePromptHotkey.Length > 0)
            Add(Show(_settings.ActivePromptHotkey), "Dictate Prompt", "Text markieren, eine Anweisung sprechen, die Auswahl wird an Ort und Stelle umgeschrieben");
        if (_settings.ActiveScreenshotHotkey.Length > 0)
            Add(Show(_settings.ActiveScreenshotHotkey), "Bildschirmfoto", "einen Bereich wählen, das Bild geht mit der nächsten Anweisung mit");
        // Mac WelcomeSteps: Chat and Vorlesen follow Bildschirmfoto.
        if (_settings.ActiveChatHotkey.Length > 0)
            Add(Show(_settings.ActiveChatHotkey), "Chat", "ein vollständiges Chatfenster, mit Bildschirmfotos und Dateien");
        if (_settings.ActiveReadAloudHotkey.Length > 0)
            Add(Show(_settings.ActiveReadAloudHotkey), "Vorlesen", "Text irgendwo markieren und vorlesen lassen");
        if (_settings.ActiveLiveDictateHotkey.Length > 0)
            Add(Show(_settings.ActiveLiveDictateHotkey), "Live diktieren", "der Text erscheint nach jeder Pause am Cursor");
        if (_settings.ActiveGlossaryHotkey.Length > 0)
            Add(Show(_settings.ActiveGlossaryHotkey), "Auswahl zum Glossar hinzufügen", "ein Wort markieren, damit es künftig richtig geschrieben wird");
        if (_settings.ActiveOpenSettingsHotkey.Length > 0)
            Add(Show(_settings.ActiveOpenSettingsHotkey), "Einstellungen", "öffnet und schließt die Einstellungen");
        Add("Esc", "Abbrechen", "verwirft eine laufende Aufnahme");
        _shortcutList.ResumeLayout(true);
        Theme.Apply(_shortcutList);
    }

    // ---------------- layout helpers ----------------

    private static TableLayoutPanel NewPage()
    {
        var page = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0), Padding = new Padding(0),
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.ControlAdded += (_, _) => page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return page;
    }

    private static TableLayoutPanel Block()
    {
        var block = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0),
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        block.ControlAdded += (_, _) => block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return block;
    }

    /// <summary>A label on the left, a control on the right.</summary>
    private static TableLayoutPanel Row(Control left, Control right)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.Anchor = AnchorStyles.Left;
        right.Anchor = AnchorStyles.Right;
        right.Margin = new Padding(16, 0, 0, 0);
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    /// <summary>Icon, title and one line of subtitle at the top of a step (Mac WelcomeStepHeader). Tag = the subtitle label.</summary>
    private Control Header(string icon, string title, string subtitle)
    {
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new ThemedLabel { Text = icon, Font = new Font(Theme.Icons.FontFamily, 24f), Tone = Tone.Accent, AutoSize = true, Margin = new Padding(0, 4, 14, 0) }, 0, 0);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        text.Controls.Add(new ThemedLabel { Text = title, Font = Theme.Section, AutoSize = true, Margin = new Padding(0) });
        var sub = Wrap(subtitle, 60, top: 4);
        sub.Tone = Tone.Secondary;
        text.Controls.Add(sub);
        header.Controls.Add(text, 1, 0);
        header.Tag = sub;
        return header;
    }

    private static Control Centered(Control c, int top = 0)
    {
        c.Anchor = AnchorStyles.None;
        if (top > 0) c.Margin = new Padding(c.Margin.Left, top, c.Margin.Right, c.Margin.Bottom);
        return c;
    }

    /// <summary>A wrapping label; its width follows the window (<see cref="FitWraps"/>) minus <paramref name="inset"/>.</summary>
    private ThemedLabel Wrap(string text, int inset, bool small = false, int top = 0)
    {
        var label = new ThemedLabel
        {
            Text = text, AutoSize = true, Font = small ? Theme.Small : Theme.Body, Tone = small ? Tone.Secondary : Tone.Primary,
            Margin = new Padding(0, top, 0, 0), MaximumSize = new Size(ContentWidth - inset, 0),
        };
        _wraps.Add((label, inset));
        return label;
    }

    private int _wrapWidth;

    private void FitWraps()
    {
        int width = _host.ClientSize.Width - _host.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        // Resize also fires for height changes and while the user drags the frame: only a new width re-wraps.
        if (width <= 100 || width == _wrapWidth) return;
        _wrapWidth = width;
        // Batched: each new MaximumSize otherwise re-lays out the nested auto-size tables of every page.
        LayoutBatch.Run(_host, () =>
        {
            foreach (var (label, inset) in _wraps) label.MaximumSize = new Size(Math.Max(120, width - LogicalToDeviceUnits(inset)), 0);
        });
    }

    private static void SetStatus(ThemedLabel label, string text, Tone tone)
    {
        label.Text = text;
        label.Tone = tone;
    }

    private static void OpenUri(string uri)
    {
        if (!uri.StartsWith("ms-settings:", StringComparison.Ordinal) && !Core.NetworkPolicy.IsAllowedBrowserUrl(uri)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Welcome: opening {uri} failed: {e.GetType().Name}"); }
    }

    /// <summary>"670 MB", "1,2 GB" (German decimal comma).</summary>
    private static string FormatBytes(long bytes)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return bytes >= 1e9 ? (bytes / 1e9).ToString("0.0", de) + " GB" : (bytes / 1e6).ToString("0", de) + " MB";
    }

    // ---------------- small controls ----------------

    /// <summary>The hairline above the footer.</summary>
    private sealed class Line : Control
    {
        public Line() { Dock = DockStyle.Fill; Margin = new Padding(0); }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override Color BackColor { get => Theme.Divider; set { } }
    }

    /// <summary>One dot per step, filled up to the current one (Mac stepIndicator).</summary>
    private sealed class StepDots : Control
    {
        private int _current;

        public StepDots()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Core.WelcomeTour.Steps.Count * 13, 10);
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Current
        {
            get => _current;
            set
            {
                _current = value;
                AccessibleName = $"Schritt {value + 1} von {Core.WelcomeTour.Steps.Count}";
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = LogicalToDeviceUnits(7), step = LogicalToDeviceUnits(13);
            for (int i = 0; i < Core.WelcomeTour.Steps.Count; i++)
            {
                using var brush = new SolidBrush(i <= _current ? Theme.Accent : Theme.Track);
                g.FillEllipse(brush, i * step, (Height - d) / 2, d, d);
            }
        }
    }

    /// <summary>A key combination as a small rounded cap (Fertig: the shortcut list).</summary>
    private sealed class KeyCap : Label
    {
        public KeyCap(string text)
        {
            Text = text;
            AutoSize = true;
            Font = Theme.Small;
            Padding = new Padding(8, 3, 8, 3);
            Margin = new Padding(0, 2, 12, 0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(4)))
            {
                using var fill = new SolidBrush(Theme.Button);
                using var pen = new Pen(Theme.ButtonBorder);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>
    /// One of the two answers to "Wie soll das Diktat laufen?" (Mac choiceCard): icon, title, tagline, three facts, and a
    /// ring that fills when chosen. Click, Space or Enter picks it; screen readers see a radio button.
    /// </summary>
    private sealed class ChoiceCard : Control
    {
        private readonly string _icon, _title, _tagline;
        private readonly string[] _facts;
        private bool _selected, _hover;

        public event Action? Picked;

        public ChoiceCard(string icon, string title, string tagline, string[] facts)
        {
            _icon = icon;
            _title = title;
            _tagline = tagline;
            _facts = facts;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Height = 150;
            Anchor = AnchorStyles.Left | AnchorStyles.Right;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.RadioButton;
            AccessibleName = title;
            AccessibleDescription = tagline + ". " + string.Join(". ", facts);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                Invalidate();
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new CardAccessible(this);

        private sealed class CardAccessible(ChoiceCard owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleStates State =>
                base.State | AccessibleStates.Focusable | (owner.Selected ? AccessibleStates.Checked : AccessibleStates.None);

            public override string DefaultAction => "Auswählen";

            public override void DoDefaultAction() => owner.Picked?.Invoke();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            Picked?.Invoke();
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Space)
            {
                Picked?.Invoke();
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float lw = _selected ? LogicalToDeviceUnits(2) : 1;
            var r = new RectangleF(lw / 2 + 0.5f, lw / 2 + 0.5f, Width - lw - 1.5f, Height - lw - 1.5f);
            using (var path = Theme.Rounded(r, LogicalToDeviceUnits(8)))
            {
                using var fill = new SolidBrush(_selected ? Theme.ItemSelected : _hover ? Theme.ButtonHover : Theme.Card);
                using var pen = new Pen(_selected ? Theme.Accent : Theme.CardBorder, lw);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                using var ring = new Pen(Theme.Text, LogicalToDeviceUnits(2)) { DashStyle = DashStyle.Dot };
                using var path = Theme.Rounded(RectangleF.Inflate(r, -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(4)), LogicalToDeviceUnits(6));
                g.DrawPath(ring, path);
            }

            int pad = LogicalToDeviceUnits(14);
            using var iconFont = new Font(Theme.Icons.FontFamily, 18f);
            TextRenderer.DrawText(g, _icon, iconFont, new Point(pad, pad + LogicalToDeviceUnits(2)), Theme.Accent, TextFormatFlags.NoPadding);
            int textX = pad + LogicalToDeviceUnits(36);
            TextRenderer.DrawText(g, _title, Theme.Strong, new Point(textX, pad), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _tagline, Theme.Small, new Point(textX, pad + LogicalToDeviceUnits(20)), Theme.Secondary, TextFormatFlags.NoPadding);

            // The ring on the right, filled when chosen.
            float d = LogicalToDeviceUnits(18);
            var circle = new RectangleF(Width - pad - d, pad + LogicalToDeviceUnits(2), d, d);
            if (_selected)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillEllipse(fill, circle);
                using var tick = new Pen(Theme.AccentText, LogicalToDeviceUnits(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = circle.X + d / 2, cy = circle.Y + d / 2, k = d / 18f;
                g.DrawLines(tick, new[] { new PointF(cx - 4 * k, cy), new PointF(cx - 1 * k, cy + 3 * k), new PointF(cx + 4.5f * k, cy - 3 * k) });
            }
            else
            {
                using var pen = new Pen(Theme.Secondary, 1.2f);
                g.DrawEllipse(pen, circle);
            }

            int y = pad + LogicalToDeviceUnits(50);
            int lineH = LogicalToDeviceUnits(22);
            foreach (var fact in _facts)
            {
                var bounds = new Rectangle(pad, y, Width - 2 * pad, lineH);
                TextRenderer.DrawText(g, "•  " + fact, Theme.Small, bounds, Theme.Secondary,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                y += lineH;
            }
        }
    }
}
