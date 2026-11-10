using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

internal enum Mode { Dictate, Prompt, Template }

/// <summary>
/// Tray icon, hotkeys and the recording state machine. Same toggle behaviour as the Mac:
/// 1st press starts, 2nd press stops and processes, a press while processing cancels.
/// Everything runs on the UI thread; no window ever takes focus during a recording.
/// </summary>
internal sealed partial class TrayApp : ApplicationContext
{
    private enum State { Idle, Recording, Processing }

    /// <summary>
    /// A recording whose processing failed for a reason a second try can fix (network, quota, local model).
    /// Also used for a take recovered after a crash. Dropped after the next success.
    /// <see cref="ClipboardOnly"/> is set for a recovered take: the original window is gone.
    /// </summary>
    private sealed record FailedRun(byte[] Wav, Mode Mode, double Seconds, string? Selection, string? QuickAction, IntPtr Target,
        Core.DictationTemplate? Template = null, string StopKey = "", bool ClipboardOnly = false,
        Core.ScreenshotImage? Screenshot = null, bool NoNewTextTemplate = false);


    private readonly string? _testWav;
    private readonly bool _fakeGemini;
    private readonly NotifyIcon _icon;
    private readonly HotkeyService _hotkeys;
    private readonly ClipboardPaster _paster;
    private readonly Core.Settings _settings;
    private readonly Core.GeminiClient _gemini;
    private readonly Core.OpenAiClient _openAi;
    private readonly OfflineEngine _offline;
    private readonly ToolStripMenuItem _statusItem;
    // Menu rows in the Mac's order (MenuBarController.createMenu); each shows its shortcut on the right.
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripSeparator _stopSeparator;
    private readonly ToolStripMenuItem _proposalsItem;
    private readonly ToolStripMenuItem _dictateItem;
    private readonly ToolStripMenuItem _promptItem;
    private readonly ToolStripMenuItem _screenshotItem;
    private readonly ToolStripMenuItem _readAloudItem;
    private readonly ToolStripMenuItem _glossaryItem;
    private readonly ToolStripMenuItem _liveItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _copyLast;
    private readonly ToolStripMenuItem _recentMenu;
    private readonly ToolStripMenuItem _retryItem;
    private readonly RecordingOverlay _overlay = new();
    // In memory only: in a practice a dictation must not stay behind in a file.
    private readonly Core.RecentTexts _recent = new();
    private static readonly Hotkey Escape = new(0, Keys.Escape);
    // Ctrl+1–9 while a Dictate Prompt recording runs: apply a quick instruction (Settings.QuickActions).
    private static readonly Hotkey[] QuickKeys =
        Enumerable.Range(0, Core.QuickActionList.Max).Select(i => new Hotkey(NativeMethods.MOD_CONTROL, Keys.D1 + i)).ToArray();

    private State _state = State.Idle;
    private Mode _mode;
    // A copy taken at the start of a template run: editing the settings meanwhile must not change this run.
    private Core.DictationTemplate? _template;
    // "Nochmal ohne Vorlage": this Dictate Prompt run leaves out the "Vorlage für neue Texte" too (the Mac has none there).
    private bool _noNewTextTemplate;
    // The stored hotkey that started the run; shown in the overlay hint and the status text.
    private string _stopKey = "";
    private Core.IAudioSource? _audio;
    private Task<string?>? _selection;
    private CancellationTokenSource? _selectionCts;
    private string? _quickAction;
    // The window in front when the recording started: the only paste target, for a retry too.
    private IntPtr _pasteTarget;
    private FailedRun? _lastFailed;

    // Live diktieren (macOS Dictate Live): Mode.Dictate plus a live session, like the Mac's isLiveDictation flag.
    // _live and _liveChunker change under _liveGate: the capture thread feeds the chunker.
    private volatile Core.LiveDictationSession? _live;
    private Core.MeetingChunker? _liveChunker;
    private readonly object _liveGate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Core.MeetingAudioChunk Chunk, long Cut)> _livePieces = new();
    private readonly System.Windows.Forms.Timer _liveRetryTimer = new() { Interval = 500 };
    // The window the live dictation types into; set once the recording runs.
    private IntPtr _liveTarget;
    /// <summary>Bumped by every recording that starts; right Ctrl remembers the one it started (Mac fnStartedRecordingID).</summary>
    private long _recordingSerial;
    private long _rightCtrlRecording = -1;
    /// <summary>Process the current live dictation inserts into (Mac liveTargetBundleId), for the last-insertion memory.</summary>
    private string? _liveTargetApp;
    /// <summary>Punctuation mode of the current live dictation, fixed at its start like Diktieren (Mac livePunctuationMode).</summary>
    private Core.PunctuationMode _livePunctuation = Core.PunctuationMode.Automatic;
    /// <summary>What the previous live dictation ended with, while nothing has moved the caret since.</summary>
    private readonly LiveInsertionMemory _lastLive;
    private IReadOnlyList<string> _unsupported = Array.Empty<string>();
    private CancellationTokenSource? _cts;
    private SettingsForm? _settingsForm;
    private WelcomeForm? _welcomeForm;
    // The settings were asked for while the tour was open: open them once it closes.
    private bool _settingsAfterTour;
    // Mac „Einrichtung abschließen …“: shown while the welcome tour is not finished.
    private readonly ToolStripMenuItem _finishSetupItem;
    private readonly Toast _toast = new();
    private readonly UpdateService _updates;
    private readonly ToolStripMenuItem _updateItem;
    // A found update waits for the end of a recording; the notice then comes a little later, so it does not
    // replace a "Bitte prüfen" balloon that was just shown.
    private readonly System.Windows.Forms.Timer _updateNoticeTimer = new() { Interval = 20_000 };
    private bool _updateNoticePending;
    private bool _glossaryBusy;
    private bool _globalHotkeysOn;
    private string _busyStatus = Core.UserCopy.Processing;
    private readonly Core.PromptConversationMemory _promptMemory = new();
    private readonly Core.HotkeyPressGate _pressGate = new();
    private readonly Core.RightCtrlDictationToggle _rightCtrl;
    private readonly System.Windows.Forms.Timer _keyUpTimer = new() { Interval = 30 };
    private Keys _armedKey;
    private long _armedKeyDeadline;
    private readonly SynchronizationContext _ui;
    // Screenshot hotkey: the image waits here for the next Dictate Prompt or template run (memory only, 5 min).
    private readonly Core.ScreenshotHold _heldShot = new(() => DateTime.UtcNow);
    private readonly System.Windows.Forms.Timer _shotExpiry = new() { Interval = 15_000 };
    // Mac transcribeCancelledRecording / runAgainWithoutTemplate: audio kept in memory only.
    private readonly Core.CancelledRecording _cancelled = new();
    private readonly Core.TemplateReplay _templateReplay = new();
    private readonly System.Windows.Forms.Timer _replayExpiry = new() { Interval = 15_000 };
    private readonly ToolStripMenuItem _cancelledItem, _withoutTemplateItem;
    private readonly ToolStripMenuItem _welcomeItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private Core.ScreenshotImage? _runShot;
    private CancellationTokenSource? _snipCts;
    private readonly MeetingController _meeting;
    private readonly McpService _mcp;
    private readonly ReadAloudController _readAloud;

    public TrayApp(string? testWav, bool fakeGemini)
    {
        _testWav = testWav;
        _fakeGemini = fakeGemini;

        bool firstRun = !File.Exists(Core.Settings.DefaultPath);
        _settings = Core.Settings.Load(Core.Settings.DefaultPath, new DpapiProtector());
        Sounds.Enabled = _settings.Sounds;
        bool testRun = testWav != null || fakeGemini;
        InstallState.TestRun = testRun; // before anything registers itself: the one "may register" rule reads it
        if (firstRun)
        {
            _settings.DictatePromptTemplate = QuoteTemplate.Default;
            TrySave();
            // On by default: otherwise the app is simply gone after the next restart and the user
            // thinks it broke. Off in the settings. Not for test runs, which would autostart a test build.
            if (!testRun) AutoStart.Enable();
        }
        else
        {
            AutoStart.RefreshPathIfEnabled(); // the .exe may have been moved out of Downloads
        }
        StartMenu.EnsureEntry(testRun); // Windows search finds the app (Mac: Spotlight finds /Applications)
        UninstallEntry.Ensure(); // Windows Settings → Apps lists it with a working Deinstallieren
        _gemini = new Core.GeminiClient(_settings);
        _openAi = new Core.OpenAiClient(_settings);
        _offline = new OfflineEngine(_settings);
        _offline.Prewarm();

        _hotkeys = new HotkeyService();
        _rightCtrl = new Core.RightCtrlDictationToggle(new RightCtrlBridge(this), () => Environment.TickCount64);
        _lastLive = new LiveInsertionMemory(IsOwnShortcut);
        _paster = new ClipboardPaster(_hotkeys.Handle);
        _hotkeys.ClipboardMessage = _paster.OnClipboardMessage; // the hotkey window owns the clipboard: delayed rendering lands there

        _statusItem = new ToolStripMenuItem(Core.UserCopy.ReadyStatus) { Enabled = false };
        // Titles are the Mac's German ones (Localizable.xcstrings).
        _stopItem = new ToolStripMenuItem("Stopp", null, (_, _) => StopCurrentOperation()) { Visible = false };
        _stopSeparator = new ToolStripSeparator { Visible = false };
        _proposalsItem = new ToolStripMenuItem("Änderungen von KI-Apps prüfen …", null, (_, _) => _mcp?.ShowPendingProposals()) { Visible = false };
        _dictateItem = new ToolStripMenuItem("Diktieren", null, (_, _) => OnHotkey(Mode.Dictate));
        _promptItem = new ToolStripMenuItem("Dictate Prompt", null, (_, _) => OnHotkey(Mode.Prompt));
        _screenshotItem = new ToolStripMenuItem("Bildschirmfoto", null, (_, _) => OnScreenshotHotkey());
        _readAloudItem = new ToolStripMenuItem("Vorlesen", null, (_, _) => OnReadAloudHotkey());
        _glossaryItem = new ToolStripMenuItem("Auswahl zum Glossar hinzufügen", null, (_, _) => _ = AddSelectionToGlossaryAsync());
        _liveItem = new ToolStripMenuItem("Live diktieren", null, (_, _) => OnLiveHotkey());
        _copyLast = new ToolStripMenuItem("Letzte Transkription kopieren", null, (_, _) => CopyRecent(_recent.Latest));
        _recentMenu = new ToolStripMenuItem("Letzte Transkriptionen");
        _retryItem = new ToolStripMenuItem("Letzte Aufnahme erneut verarbeiten", null, (_, _) => RetryLast()) { Visible = false };
        _cancelledItem = new ToolStripMenuItem("Abgebrochene Aufnahme transkribieren", null, (_, _) => TranscribeCancelledRecording()) { Visible = false };
        _withoutTemplateItem = new ToolStripMenuItem("Nochmal ohne Vorlage", null, (_, _) => RunAgainWithoutTemplate()) { Visible = false };
        // An update restarts the app: not in the middle of a dictation or a meeting.
        _updates = new UpdateService(_settings, () => _state == State.Idle && _meeting?.IsActive != true && _readAloud?.IsActive != true, Quit);
        _updateItem = new ToolStripMenuItem("Update installieren …", null, (_, _) => _updates.ShowDialog())
        {
            Visible = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        _settingsItem = new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings());
        _welcomeItem = new ToolStripMenuItem("Einführung …", null, (_, _) => OpenWelcome());
        // Mac #346: „Nach Updates suchen …“ in the status menu (the app menu of a menu bar app); the switch stays in Allgemein → Updates.
        _checkUpdatesItem = new ToolStripMenuItem("Nach Updates suchen …", null, (_, _) => _ = CheckForUpdatesFromTrayAsync());
        _finishSetupItem = new ToolStripMenuItem("Einrichtung abschließen …", null, (_, _) => OpenWelcome()) { Visible = false };
        // Mac: „Feedback senden“ with the problem report first. Windows adds the log, which the report points to.
        var feedbackItem = new ToolStripMenuItem("Feedback senden");
        AddFeedbackItems(feedbackItem); // TrayApp.Feedback.cs
        var quitItem = new ToolStripMenuItem("WhisperShortcut beenden", null, (_, _) => Quit());
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // Popups sit above the recording pill while it is on screen (Mac centerBottomPillClearance).
        _toast.AvoidBounds = () => _overlay.Visible ? _overlay.Bounds : null;
        _meeting = new MeetingController(_settings, _ui, TranscribeMeetingChunkAsync, SummarizeMeetingAsync, MeetingUnavailable,
            (title, text, icon, open) => Notify(title, text, icon, open, open == null ? null : Core.UserCopy.ActionOpenFolder), TrySave, () => new[] { _settings.ActiveDictateHotkey, _settings.ActivePromptHotkey, _settings.ActiveGlossaryHotkey,
                    _settings.ActiveScreenshotHotkey, _settings.TemplateHotkey, _settings.ActiveLiveDictateHotkey,
                    _settings.ActiveOpenSettingsHotkey, _settings.ActiveReadAloudHotkey, _settings.ActiveChatHotkey }
                .Concat(_settings.Templates.Select(t => t.Hotkey)).Where(k => k.Length > 0).ToArray(),
            suspend => { if (suspend) { _hotkeys.UnregisterAll(); _globalHotkeysOn = false; } else SyncGlobalHotkeys(notify: true); },
            fakeAudio: testWav != null || fakeGemini);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, _updateItem, new ToolStripSeparator(),
            _finishSetupItem, _proposalsItem, _stopItem, _stopSeparator,
            _dictateItem, _promptItem, _screenshotItem, _readAloudItem, _glossaryItem, _liveItem, new ToolStripSeparator(),
            // Mac order: Copy Last, Recent, Transcribe Cancelled Recording, Run Again Without Template; then Windows' retry.
            _copyLast, _recentMenu, _cancelledItem, _withoutTemplateItem, _retryItem, new ToolStripSeparator(),
            // Mac: „Chat“ above Settings, and the meeting starts from the chat (/meeting); Windows keeps its Meeting row too.
            ChatMenuItem(), _meeting.MenuItem, new ToolStripSeparator(),
            _settingsItem, _welcomeItem, _checkUpdatesItem, feedbackItem, quitItem,
        });
        menu.Opening += (_, _) =>
        {
            UpdateRecentMenu();
            UpdateMenuRows();
        };
        UpdateRecentMenu();

        _icon = new NotifyIcon { Icon = TrayIcons.Ready, ContextMenuStrip = menu, Visible = true };
        // One left click opens the settings (Click alone would also fire on right click, which opens the menu).
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _mcp = new McpService(_settings, _ui, () => _state != State.Idle, () => _settingsForm?.HasUnsavedChanges == true, AfterMcpApply);
        _mcp.StartIfEnabled();
        ScheduleSpareSettings(5000);
        _readAloud = new ReadAloudController(_settings, _paster, _gemini, _openAi, (t, m, i, a) => Notify(t, m, i, a), () => ShowSettings(), TrySave, _ui);
        _readAloud.StateChanged += () =>
        {
            if (_state == State.Idle) SetState(State.Idle);
            UpdateMenuRows();
        };

        _keyUpTimer.Tick += (_, _) => PollArmedKey();
        // Windows equivalent of the Mac's didActivateApplication retry: the dictated-into window is in front again.
        _liveRetryTimer.Tick += (_, _) =>
        {
            if (_live is { HasHeldText: true } session && ClipboardPaster.ForegroundRoot() == _liveTarget) session.RetryHeld();
        };
        _replayExpiry.Tick += (_, _) =>
        {
            if (_templateReplay.DropIfExpired(DateTime.UtcNow))
            {
                Core.Log.Info("Template replay dropped: unused for 10 minutes");
                _replayExpiry.Stop();
            }
        };
        _shotExpiry.Tick += (_, _) =>
        {
            if (_heldShot.DropIfExpired()) Core.Log.Info("Screenshot dropped: unused for 5 minutes");
            if (!_heldShot.HasImage) _shotExpiry.Stop();
        };
        RegisterHotkeys();
        SetState(State.Idle);
        // A finished download changes the idle status ("Modelle noch nicht geladen" → "Bereit").
        _offline.DownloadChanged += () => { if (_state == State.Idle) SetState(State.Idle); };
        _overlay.QuickActionClicked += OnQuickAction;
        _overlay.CancelClicked += OnEscape; // the pill's ✕ (Mac): discard the recording or cancel the processing
        _updates.Found += OnUpdateFound;
        _updateNoticeTimer.Tick += (_, _) =>
        {
            _updateNoticeTimer.Stop();
            if (_state == State.Idle) ShowUpdateNotice();
            else _updateNoticePending = true;
        };
        // Test runs (VM loop) must not pop update notices into the screenshots.
        if (!testRun) _updates.Start();
        _ui.Post(_ => _meeting.OnStartup(), null);

        // Mac: the tour opens at every start until it was finished with „Fertig“ (closing it is not finishing).
        if (!testRun && !_settings.WelcomeCompleted)
        {
            OpenWelcome();
        }
        else if (!testRun && OfferRecoveredRecording())
        {
            // A leftover take is more urgent than the missing-key welcome.
        }
        else if (MissingKey)
        {
            Notify("Willkommen bei WhisperShortcut",
                "Trag zuerst einen API-Schlüssel ein oder schalte den Offline-Modus ein.",
                ToolTipIcon.Info, ShowSettings, Core.UserCopy.ActionOpenSettings);
        }
        // Posted after OpenWelcome's own post: the tour window is up first, the question comes on top of it.
        if (!testRun) _ui.Post(_ => OfferMoveToFixedPlace(), null);
    }

    /// <summary>
    /// After a crash or a kill mid-recording the spill file is still there. Offer to transcribe it;
    /// the original window is gone, so the text stays on the clipboard.
    /// </summary>
    private bool OfferRecoveredRecording()
    {
        var rec = Core.RecordingCheckpoint.TryRecover();
        if (rec == null) return false;
        var mode = string.Equals(rec.Mode, "Prompt", StringComparison.OrdinalIgnoreCase) ? Mode.Prompt : Mode.Dictate;
        // A template take is recovered as a plain dictation: the template is not stored with the audio.
        bool wasTemplate = string.Equals(rec.Mode, nameof(Mode.Template), StringComparison.OrdinalIgnoreCase);
        _lastFailed = new FailedRun(rec.Wav, mode, rec.Seconds, null, null, IntPtr.Zero,
            StopKey: mode == Mode.Prompt ? _settings.PromptHotkey : _settings.DictateHotkey, ClipboardOnly: true);
        Notify("Aufnahme unterbrochen",
            $"Eine {rec.Seconds:0} s lange Aufnahme war noch nicht fertig. Der Text landet nach dem Transkribieren{(wasTemplate ? " ohne Vorlage" : "")} in der Zwischenablage.",
            ToolTipIcon.Warning, RetryLast, "Transkribieren");
        return true;
    }

    // ---------------- hotkeys ----------------

    private string _lastWarnedHotkeys = "";
    // Set by Save in the settings window: the next registration warns about a newly chosen key that is taken.
    private bool _hotkeyCheckPending;

    /// <param name="notify">false when only re-arming after the settings window lost focus: no repeated warning.</param>

    private void RegisterHotkeys(bool notify = true)
    {
        _globalHotkeysOn = true;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        // Active* is "" when a shortcut is switched off (Mac isEnabled) or unassigned; those are not registered.
        var keys = new List<(string, Action)>
        {
            (_settings.ActiveDictateHotkey, () => OnHotkey(Mode.Dictate)),
            (_settings.ActivePromptHotkey, () => OnHotkey(Mode.Prompt)),
            (_settings.ActiveGlossaryHotkey, () => _ = AddSelectionToGlossaryAsync()),
            // No meeting hotkey by default: the tray menu starts a meeting.
            (_settings.MeetingHotkey, _meeting.Toggle),
            // Live diktieren ships switched off (Mac: disabled).
            (_settings.ActiveLiveDictateHotkey, OnLiveHotkey),
            (_settings.ActiveScreenshotHotkey, OnScreenshotHotkey),
            (_settings.ActiveReadAloudHotkey, OnReadAloudHotkey),
            (_settings.ActiveChatHotkey, ToggleChat), // TrayApp.Chat.cs
            (_settings.ActiveOpenSettingsHotkey, ToggleSettings),
        };
        foreach (var (stored, action) in keys)
        {
            if (string.IsNullOrWhiteSpace(stored)) continue;
            // RightCtrl alone needs the keyboard hook, which RegisterHotKey is not: report it instead of registering
            // a bare VK_RCONTROL.
            if (!Hotkey.TryParse(stored, out var key) || key.IsRightCtrl || !_hotkeys.Register(key, action))
                failed.Add(Hotkey.Display(stored));
        }
        // Right Ctrl alone = the Mac's Fn key (on by default, Mac fnKeyDictation).
        if (_settings.RightCtrlDictation && !_hotkeys.RegisterRightCtrl(_rightCtrl))
            failed.Add(Core.HotkeyText.RightCtrlDisplay);

        // Template keys come after the app's own, which win on a clash (see TemplateHotkeys).
        var appHotkeys = new (string Name, string Hotkey)[]
        {
            ("Diktieren", _settings.ActiveDictateHotkey),
            ("Text bearbeiten", _settings.ActivePromptHotkey),
            ("Glossar", _settings.ActiveGlossaryHotkey),
            ("Screenshot", _settings.ActiveScreenshotHotkey),
            ("Vorlesen", _settings.ActiveReadAloudHotkey),
            ("Chat", _settings.ActiveChatHotkey),
            ("Live Meeting", _settings.MeetingHotkey),
            ("Live diktieren", _settings.ActiveLiveDictateHotkey),
            ("Einstellungen", _settings.ActiveOpenSettingsHotkey),
        };
        var templates = _settings.Templates;
        var pickerActive = Core.TemplateHotkeys.PickerIsActive(_settings.TemplateHotkey, templates, appHotkeys, CanonicalHotkey);
        if (pickerActive)
        {
            var stored = _settings.TemplateHotkey;
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, () => OnTemplateHotkey(null, stored)))
                failed.Add(Hotkey.Display(stored));
        }
        var bindings = Core.TemplateHotkeys.ActiveBindings(templates, appHotkeys, _settings.TemplateHotkey, CanonicalHotkey);
        foreach (var (template, stored) in bindings)
        {
            var id = template.Id;
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, () => OnTemplateHotkey(id, stored)))
                failed.Add(Hotkey.Display(stored));
        }
        // Counts and positions only: template names are the user's own words.
        var skipped = templates.Select((t, i) => (t, i))
            .Where(x => x.t.IsUsable && CanonicalHotkey(x.t.Hotkey) != null && !bindings.Any(b => b.Template.Id == x.t.Id))
            .Select(x => x.i + 1).ToList();
        if (skipped.Count > 0)
            Core.Log.Info($"Template hotkeys: {skipped.Count} skipped, key already owned (template position {string.Join(", ", skipped)})");
        SyncSessionHotkeys();
        UpdateMenuRows();
        // WHY remembered: the settings window closing re-registers with notify, which would repeat the same warning.
        var failedKey = string.Join("|", failed.OrderBy(n => n, StringComparer.Ordinal));
        if (failed.Count == 0) _lastWarnedHotkeys = "";
        else if (notify && failedKey != _lastWarnedHotkeys)
        {
            _lastWarnedHotkeys = failedKey;
            Notify("Tastenkürzel belegt",
                $"{string.Join(" und ", failed)} {(failed.Count > 1 ? "werden" : "wird")} schon von einem anderen Programm benutzt. "
                + "Wähl in den Einstellungen ein anderes.",
                ToolTipIcon.Warning, ShowSettings, Core.UserCopy.ActionOpenSettings);
        }
    }

    /// <summary>
    /// The menu rows as the Mac shows them (makeItem, updateUI): every action row is clickable and shows its shortcut
    /// when it is on; Diktieren and Dictate Prompt read „… beenden“ while their own recording runs; „Stopp“ and the
    /// recovery rows only appear when they can do something.
    /// </summary>
    private void UpdateMenuRows()
    {
        static string Key(string active) => active.Length > 0 ? Hotkey.Display(active) : "";
        bool idle = _state == State.Idle;
        _stopItem.Visible = _stopSeparator.Visible = !idle || _readAloud?.IsActive == true;
        _proposalsItem.Visible = _mcp != null && _mcp.Proposals.Pending.Count > 0;
        _finishSetupItem.Visible = !_settings.WelcomeCompleted;
        _welcomeItem.Visible = _settings.WelcomeCompleted; // Mac: „Einführung …“ hides while „Einrichtung abschließen …“ shows
        bool recording = _state == State.Recording;
        _dictateItem.Text = recording && _mode == Mode.Dictate && _live == null ? "Diktat beenden" : "Diktieren";
        _dictateItem.ShortcutKeyDisplayString = Key(_settings.ActiveDictateHotkey);
        _promptItem.Text = recording && _mode == Mode.Prompt ? "Prompt-Diktat beenden" : "Dictate Prompt";
        _promptItem.ShortcutKeyDisplayString = Key(_settings.ActivePromptHotkey);
        _screenshotItem.ShortcutKeyDisplayString = Key(_settings.ActiveScreenshotHotkey);
        _readAloudItem.Text = _readAloud?.IsActive == true ? "Vorlesen beenden" : "Vorlesen";
        _readAloudItem.ShortcutKeyDisplayString = Key(_settings.ActiveReadAloudHotkey);
        UpdateChatRow();
        _glossaryItem.ShortcutKeyDisplayString = Key(_settings.ActiveGlossaryHotkey);
        _liveItem.ShortcutKeyDisplayString = Key(_settings.ActiveLiveDictateHotkey);
        _settingsItem.ShortcutKeyDisplayString = Key(_settings.ActiveOpenSettingsHotkey);
        // Mac: both history rows hide while the history is empty.
        _copyLast.Visible = _recent.Latest != null;
        _recentMenu.Visible = _recent.Items.Count > 0;
        _retryItem.Visible = _lastFailed != null && idle;
        _cancelledItem.Visible = _cancelled.HasRecording;
        var replay = _templateReplay.Current(DateTime.UtcNow);
        _withoutTemplateItem.Visible = replay != null;
        if (replay != null)
            _withoutTemplateItem.ToolTipText = $"Führt dein letztes Diktat noch einmal mit dem System-Prompt von Dictate Prompt aus statt mit „{replay.Value.TemplateName}“. "
                + "Das Ergebnis kommt in die Zwischenablage.";
    }

    /// <summary>The menu's „Stopp“ (Mac stopCurrentOperation): stops a recording, cancels the processing.</summary>
    private void StopCurrentOperation()
    {
        Core.Log.Info($"Menu Stopp in state {_state}");
        if (_state == State.Recording) _ = StopAndProcessAsync();
        else if (_state == State.Processing) _cts?.Cancel();
        else if (_readAloud.IsActive) _readAloud.Stop();
    }

    private void OnHotkey(Mode mode)
    {
        var stored = mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey;
        // Stop, cancel and busy stay as they are: the Diktieren key ends whichever recording runs, Live included (Mac).
        HandleHotkeyPress(mode, stored, canStopOrCancel: true,
            () => { if (mode == Mode.Dictate) StartDictateByMode(stored, "Diktieren shortcut"); else StartRecording(mode, stored); });
    }

    /// <summary>
    /// The Diktieren key and right Ctrl start what Diktieren → Modus says (Mac toggleDictation / fnDictationStart with
    /// LiveDictation.startDecision). Live that cannot start says why and starts nothing: no silent fallback. During a
    /// live meeting the decision falls back to a normal Diktat, as on the Mac.
    /// </summary>
    private void StartDictateByMode(string stored, string source)
    {
        var (decision, reason) = Core.LiveDictation.StartDecision(_settings.DictateMode, LiveUnavailable());
        switch (decision)
        {
            case Core.LiveStartDecision.StartDictate:
                StartRecording(Mode.Dictate, stored);
                break;
            case Core.LiveStartDecision.StartLive:
                Core.Log.Info($"SHORTCUTS: {source} starts Live diktieren (mode)");
                StartLiveDictation(stored);
                break;
            default:
                PresentLiveUnavailable(reason!.Value);
                break;
        }
    }

    private Core.LiveUnavailable? LiveUnavailable() => Core.LiveDictation.UnavailableReason(_meeting.IsActive,
        _settings.EffectiveSpeechEngine == Core.Engine.Local, OfflineEngine.SpeechInstalled);

    /// <summary>A key press (key plus held modifiers) that is one of our own shortcuts: it must not erase the last-insertion memory.</summary>
    private bool IsOwnShortcut(Keys pressed) =>
        new[] { _settings.ActiveDictateHotkey, _settings.ActivePromptHotkey, _settings.ActiveLiveDictateHotkey }
            .Any(stored => Hotkey.TryParse(stored, out var h) && (h.Key
                | ((h.Modifiers & NativeMethods.MOD_CONTROL) != 0 ? Keys.Control : 0)
                | ((h.Modifiers & NativeMethods.MOD_ALT) != 0 ? Keys.Alt : 0)
                | ((h.Modifiers & NativeMethods.MOD_SHIFT) != 0 ? Keys.Shift : 0)) == pressed);

    private void PresentLiveUnavailable(Core.LiveUnavailable why)
    {
        Core.Log.Info($"LIVE-DICTATE: not started ({why})");
        Sounds.Error();
        Notify(Core.UserCopy.LiveTitle, Core.LiveDictation.Message(why), ToolTipIcon.Info, ShowSettings, Core.UserCopy.ActionOpenSettings);
    }

    /// <summary>
    /// What the right Ctrl toggle drives (Mac: MenuBarController as FnDictationToggleDelegate). It starts what Diktieren →
    /// Modus says and ends only the recording it started itself (Diktat or Live).
    /// </summary>
    private sealed class RightCtrlBridge(TrayApp app) : Core.IRightCtrlDictationDelegate
    {
        public bool Start()
        {
            if (app._state != State.Idle) return false;
            Core.Log.Info("Hotkey RightCtrl in state Idle");
            app.StartDictateByMode(Core.HotkeyText.RightCtrl, "Right Ctrl");
            // Right Ctrl ends only the recording it started, never one another shortcut began, even of the same kind.
            app._rightCtrlRecording = app._state == State.Recording ? app._recordingSerial : -1;
            return IsRecording();
        }

        public void Stop()
        {
            if (IsRecording()) _ = app.StopAndProcessAsync();
        }

        public void Discard()
        {
            if (IsRecording()) app.OnEscape();
        }

        public bool IsRecording() => app._state == State.Recording && app._mode == Mode.Dictate
            && app._recordingSerial == app._rightCtrlRecording;

        public bool IsProcessing() => app._state == State.Processing && app._mode == Mode.Dictate;

        public void CancelProcessing() => app._cts?.Cancel();
    }

    private void OnLiveHotkey()
    {
        var stored = _settings.LiveDictateHotkey;
        HandleHotkeyPress(Mode.Dictate, stored, canStopOrCancel: true, () => StartLiveDictation(stored));
    }

    /// <summary>
    /// A template key, or the generic picker key (<paramref name="templateId"/> null). Only a template run is
    /// stopped or cancelled by it: a template key must not end a Dictate or Dictate Prompt recording (Mac).
    /// </summary>
    private void OnTemplateHotkey(string? templateId, string stored) =>
        HandleHotkeyPress(Mode.Template, stored, canStopOrCancel: _mode == Mode.Template, () =>
        {
            var found = templateId == null
                ? Core.TemplatePicker.Pick(_settings.Templates, _settings.LastTemplateId)
                : _settings.Templates.FirstOrDefault(t => t.Id == templateId && t.IsUsable);
            if (found == null)
            {
                Core.Log.Info("Template hotkey: no usable template");
                Sounds.Error();
                Notify("Keine Vorlage", "Erst in den Einstellungen unter „Vorlagen“ eine Vorlage mit Anweisungen anlegen.",
                    ToolTipIcon.Info, ShowSettings);
                return;
            }
            _template = found.Clone();
            StartRecording(Mode.Template, stored);
            if (_state == State.Recording)
            {
                _settings.LastTemplateId = found.Id;
                TrySave();
            }
            else
            {
                _template = null;
            }
        });

    /// <summary>
    /// The toggle shared by every hotkey: idle starts, a press while recording stops, while processing cancels.
    /// Presses the gate rejects, or that may not stop the current run, are only logged.
    /// </summary>
    private void HandleHotkeyPress(Mode mode, string stored, bool canStopOrCancel, Action startIdle)
    {
        var label = mode.ToString();
        Core.Log.Info($"Hotkey {label} in state {_state}");
        // Vorlesen copies the selection while preparing: a second copy now could lose the user's clipboard.
        if (_state == State.Idle && mode != Mode.Dictate && _readAloud?.IsActive == true && !_readAloud.IsSpeaking)
        {
            Core.Log.Info($"Hotkey {label} ignored: Vorlesen is preparing");
            Sounds.Error();
            return;
        }
        var now = Environment.TickCount64;
        var gateOpen = _state == State.Idle || _pressGate.Accept(now);
        if (_state != State.Idle && !gateOpen)
        {
            Core.Log.Info($"Hotkey {label} in state {_state} ignored: {(_pressGate.Released ? "within 300 ms" : "key still held")}");
            return;
        }
        if (_state != State.Idle && !canStopOrCancel)
        {
            Core.Log.Info($"Hotkey {label} in state {_state} ignored: a {_mode} run is active");
            return;
        }
        var decision = Core.RecordingHotkeyPolicy.Decide(
            recording: _state == State.Recording,
            processing: _state == State.Processing,
            sameMode: mode == _mode,
            gateOpen: gateOpen);
        if (decision == Core.RecordingHotkeyDecision.Ignore)
        {
            Core.Log.Info($"Hotkey {label} in state {_state} ignored: other mode is already running");
            return;
        }
        var before = _state;
        switch (decision)
        {
            case Core.RecordingHotkeyDecision.Start:
                startIdle();
                break;
            case Core.RecordingHotkeyDecision.Stop:
                _ = StopAndProcessAsync();
                break;
            case Core.RecordingHotkeyDecision.Cancel:
                _cts?.Cancel();
                break;
        }
        if (_state != before && Hotkey.TryParse(stored, out var key))
            ArmPressGate(key.Key, now);
    }

    /// <summary>The comparable form of a stored hotkey, or null when blank or unparseable.</summary>
    private static string? CanonicalHotkey(string stored) => Hotkey.TryParse(stored, out var h) ? h.ToString() : null;

    /// <summary>
    /// The press that changed the state is still down for a moment. Polling its key tells the gate when
    /// it is up: a second WM_HOTKEY before that is a repeat or a bounce, not a stop or cancel.
    /// </summary>
    private void ArmPressGate(Keys key, long now)
    {
        _pressGate.Armed(now);
        _armedKey = key;
        _armedKeyDeadline = now + 10_000;
        _keyUpTimer.Start();
    }

    private void PollArmedKey()
    {
        bool down = (NativeMethods.GetAsyncKeyState((int)_armedKey) & 0x8000) != 0;
        // 10 s at most: a key stuck down must neither keep a timer running nor close the gate for good.
        if (!down || Environment.TickCount64 >= _armedKeyDeadline)
        {
            _pressGate.KeyUp();
            _keyUpTimer.Stop();
        }
    }

    /// <summary>
    /// Esc is a global hotkey only while a recording or the processing runs, so it never takes Esc away
    /// from other programs otherwise. It discards the recording or cancels the request (nothing is pasted).
    /// </summary>
    private void OnEscape()
    {
        Core.Log.Info($"Esc in state {_state}");
        // Esc in the screenshot picker cancels the picker, not the recording behind it.
        if (_snipCts != null) { _snipCts.Cancel(); return; }
        if (_state == State.Processing)
        {
            _cts?.Cancel(); // same path as a hotkey press during processing
            return;
        }
        if (_state != State.Recording) return;
        (_audio as MicAudioSource)?.Cancel(discardCheckpoint: true);
        Core.RecordingCheckpoint.DiscardAll();
        _audio = null;
        if (_live != null)
        {
            // Text already inserted stays: it is in another window.
            _paster.EndLive(LiveClipboardAtEnd(null, false), null);
            DiscardLive();
        }
        CancelSelectionCopy(); // no Ctrl+C into the user's window after the cancel
        _runShot = null; // discarded with the recording
        Sounds.Stop();
        _overlay.HideOverlay();
        SetState(State.Idle);
    }

    // Live diktieren ends: the clipboard setting decides between restore, keep and clear (Mac "After auto-paste").
    private Core.LiveClipboardEnd LiveClipboardAtEnd(Core.LiveDictationOutcome? outcome, bool fallbackHasText) =>
        Core.LiveDictation.ClipboardAtEnd(_settings.RestoreClipboard,
            clearAfterPaste: _settings.ClipboardAfterPaste == Core.ClipboardAfterPaste.Clear, outcome, fallbackHasText);

    private void CancelSelectionCopy()
    {
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;
        _selection = null;
        _paster.CancelLateWatch(); // Esc after the deadline: a late copy is no longer ours to restore
    }

    /// <summary>
    /// A quick instruction was picked while recording (Ctrl+N or a click on the card): stop and apply
    /// it to the selection, or with nothing marked to what was just said.
    /// </summary>
    private void OnQuickAction(int index)
    {
        if (_state != State.Recording || _mode != Mode.Prompt || index >= _settings.QuickActions.Count) return;
        Core.Log.Info($"Quick action {index + 1}");
        _quickAction = _settings.QuickActions[index];
        _ = StopAndProcessAsync();
    }

    // ---------------- read aloud ----------------

    /// <summary>
    /// Vorlesen hotkey and menu row (Mac triggerReadSelectedTextAloud): a second press stops it. Ignored while a recording
    /// or its processing runs and during a meeting, like the Mac; speaking itself does not block a new dictation.
    /// </summary>
    private void OnReadAloudHotkey()
    {
        Core.Log.Info($"Hotkey ReadAloud in state {_state}, reading {_readAloud.IsActive}");
        if (_readAloud.IsActive)
        {
            _readAloud.Toggle();
            return;
        }
        if (_state != State.Idle || _glossaryBusy || _selection != null || _meeting.IsActive)
        {
            Sounds.Error();
            return;
        }
        _readAloud.Toggle();
    }

    // ---------------- screenshot ----------------

    /// <summary>
    /// Screenshot hotkey: opens the area picker and holds the image for the next Dictate Prompt or template run.
    /// A second press while the picker is open stops it. Like the Mac also while recording: during a Dictate Prompt
    /// or template recording the image goes with that very instruction.
    /// </summary>
    private void OnScreenshotHotkey()
    {
        Core.Log.Info($"Hotkey Screenshot in state {_state}");
        if (_snipCts != null)
        {
            _snipCts.Cancel();
            return;
        }
        if (_state == State.Processing) return; // the request is already on its way
        // Like the Mac the screenshot always lands on the clipboard; only sending it with the next instruction
        // depends on „Screenshots mitsenden“ (and never offline).
        _ = CaptureScreenshotAsync();
    }

    private async Task CaptureScreenshotAsync()
    {
        using var cts = _snipCts = new CancellationTokenSource();
        try
        {
            var image = await ScreenshotCapture.CaptureAsync(_paster, cts.Token);
            if (image == null) return; // cancelled in the snip UI or timed out; the log says which
            if (!Core.ScreenshotPolicy.Allowed(_settings))
            {
                ShowDoneIfFree("Bildschirmfoto in der Zwischenablage");
                return;
            }
            // Recording a Dictate Prompt or template right now: the picture belongs to this instruction.
            if (_state == State.Recording && _mode != Mode.Dictate && _runShot == null)
            {
                _runShot = image;
                Core.Log.Info($"Screenshot attached to the running {_mode} ({image.Data.Length / 1024} KB)");
                return;
            }
            _heldShot.Hold(image);
            _shotExpiry.Start();
            Core.Log.Info($"Screenshot held for the next turn ({image.Data.Length / 1024} KB)");
            ShowDoneIfFree("Bildschirmfoto in der Zwischenablage und für die nächste Anweisung gemerkt");
        }
        catch (OperationCanceledException) { Core.Log.Info("Snip: waiting stopped by the hotkey"); }
        catch (Exception e)
        {
            Core.Log.Error("Snip failed", e);
            Notify("Screenshot fehlgeschlagen", "Das Bild konnte nicht übernommen werden. Details stehen im Protokoll.", ToolTipIcon.Warning);
        }
        finally { _snipCts = null; }
    }

    /// <summary>Screenshots switched off or offline: a held image must not go out later.</summary>
    private void DropScreenshotIfNotAllowed()
    {
        if (Core.ScreenshotPolicy.Allowed(_settings) || !_heldShot.HasImage) return;
        _heldShot.Clear();
        Core.Log.Info("Screenshot dropped: screenshots are off or offline");
    }

    // ---------------- recording ----------------

    /// <summary>
    /// The engines a mode uses: Dictate only speech recognition; Dictate Prompt the text engine and
    /// speech recognition too (the instruction is transcribed, or checked for a bare "korrigiere").
    /// </summary>
    private IEnumerable<Core.Engine> EnginesFor(Mode mode) => mode == Mode.Dictate
        ? new[] { _settings.EffectiveSpeechEngine }
        : new[] { _settings.EffectiveSpeechEngine, _settings.EffectiveTextEngine };

    /// <summary>HEAD the cloud API origin so TCP+TLS is done before the user presses Stop.</summary>
    private void PrewarmCloudConnections(Mode mode)
    {
        if (_fakeGemini) return;
        foreach (var engine in EnginesFor(mode).Distinct())
        {
            if (engine == Core.Engine.Gemini) _ = _gemini.PrewarmConnection();
            else if (engine == Core.Engine.OpenAI) _ = _openAi.PrewarmConnection();
        }
    }

    /// <summary>The provider whose key a mode needs but lacks (null: all there). Local engines need none.</summary>
    private string? MissingKeyFor(Mode mode)
    {
        if (_fakeGemini) return null;
        var engines = EnginesFor(mode).ToList();
        if (engines.Contains(Core.Engine.Gemini) && string.IsNullOrWhiteSpace(_settings.ApiKey)) return "Gemini";
        if (engines.Contains(Core.Engine.OpenAI) && string.IsNullOrWhiteSpace(_settings.OpenAiKey)) return "OpenAI";
        return null;
    }

    /// <summary>Any mode lacks a key: for the tray tooltip.</summary>
    private bool MissingKey => MissingKeyFor(Mode.Prompt) != null;

    private void StartRecording(Mode mode, string stored)
    {
        // The snip UI is open: its image is about to arrive over the clipboard, a Ctrl+C now would collide with it.
        if (_snipCts != null)
        {
            Core.Log.Info($"Hotkey {mode} ignored: waiting for the snip UI");
            return;
        }
        if (!OfflineModelsReady(mode))
        {
            Core.Log.Info($"Hotkey {mode} with a local engine, models missing");
            Sounds.Error();
            Notify(Core.UserCopy.ModelNotDownloadedTitle,
                (mode == Mode.Dictate || !OfflineEngine.SpeechInstalled
                    ? "Die Offline-Spracherkennung ist noch nicht geladen."
                    : "Das Sprachmodell für Dictate Prompt ist noch nicht geladen.")
                + (mode == Mode.Dictate || !OfflineEngine.SpeechInstalled
                    ? " Du lädst es in den Einstellungen unter Diktieren."
                    : " Du lädst es in den Einstellungen unter Dictate Prompt."),
                ToolTipIcon.Warning, ShowSettings, Core.UserCopy.ActionOpenSettings);
            return;
        }
        if (mode != Mode.Dictate && _settings.EffectiveTextEngine == Core.Engine.Local && OfflineEngine.LlmUnsupportedCpu)
        {
            Core.Log.Info($"Hotkey {mode} with the local text engine, but this CPU cannot run the language model");
            Sounds.Error();
            Notify(Core.UserCopy.OfflineModelErrorTitle, Core.LlamaServerHost.UnsupportedCpuMessage, ToolTipIcon.Warning, ShowSettings, Core.UserCopy.ActionOpenSettings);
            return;
        }
        if (MissingKeyFor(mode) is { } provider)
        {
            Core.Log.Info($"Hotkey pressed but no {provider} key (Schlüssel fehlt)");
            Sounds.Error();
            Notify(Core.UserCopy.ProviderErrorTitle(Core.GeminiErrorKind.NoKey, provider), $"Noch kein {provider}-Schlüssel eingetragen. Du trägst ihn in den Einstellungen unter „Konten“ ein.",
                ToolTipIcon.Warning, () => ShowSettingsPage(SettingsForm.AccountsPage), Core.UserCopy.ActionOpenSettings);
            return;
        }

        // How long the press waits for its feedback (Mac: the pill is there at once); the log line below says where it went.
        var feedback = Stopwatch.StartNew();
        _mode = mode;
        _stopKey = stored;
        _quickAction = null;
        _noNewTextTemplate = false;
        // Mac pastes only into the app that was in front now; the hotkey was pressed in the user's window.
        _pasteTarget = ClipboardPaster.ForegroundRoot();
        // Copy the selection right away, while the user's selection is still there. It runs while recording.
        _selectionCts = mode != Mode.Dictate ? new CancellationTokenSource() : null;
        _selection = _selectionCts != null ? _paster.CopySelectionAsync(_selectionCts.Token) : null;
        // Loading the language model takes seconds to half a minute; do it while the user speaks.
        if (mode != Mode.Dictate && _settings.EffectiveTextEngine == Core.Engine.Local) _offline.PrewarmLlm();
        // TLS to Gemini/OpenAI while the user speaks: the first request after Stop skips the handshake.
        PrewarmCloudConnections(mode);

        // WHY Template gets the long limit: its path transcribes in chunks and sends text, so it is not bound by the
        // 5-minute limit of the inline audio Dictate Prompt sends to Gemini.
        var limit = mode == Mode.Prompt ? MicAudioSource.PromptLimit : MicAudioSource.DictateLimit;
        MicAudioSource? mic = null;
        if (_testWav != null)
        {
            _audio = new Core.FileAudioSource(_testWav);
        }
        else
        {
            mic = new MicAudioSource(limit, _settings.Microphone, checkpointMode: mode.ToString());
            if (_live is { } liveSession) mic.PcmAvailable += (buffer, n) => FeedLive(liveSession, buffer, n);
            mic.MaxDurationReached += () => _ui.Post(_ =>
            {
                if (_state != State.Recording) return;
                Notify("Aufnahme beendet", $"Nach {limit.TotalMinutes:0} Minuten endet die Aufnahme automatisch. Der Text wird jetzt verarbeitet.", ToolTipIcon.Info);
                _ = StopAndProcessAsync();
            }, null);
            mic.DeviceLost += () => _ui.Post(_ =>
            {
                if (_state != State.Recording) return;
                Notify("Mikrofon getrennt", "Aufnahme beendet, das Bisherige wird verarbeitet.", ToolTipIcon.Warning);
                _ = StopAndProcessAsync();
            }, null);
            mic.CheckpointFailed += () => _ui.Post(_ =>
            {
                Notify("Datenträger voll", Core.DiskSpace.RecordingMessage, ToolTipIcon.Warning);
            }, null);
            _audio = mic;
        }

        long beforeMic = feedback.ElapsedMilliseconds;
        try
        {
            _audio.Start();
        }
        catch (Exception e)
        {
            Core.Log.Error("Recording start failed", e);
            _audio = null;
            if (_live != null) DiscardLive();
            CancelSelectionCopy();
            Sounds.Error();
            Notify("Aufnahme nicht möglich",
                e is MicrophoneUnavailableException ? e.Message : Core.UserCopy.TechnicalFallback("Die Aufnahme konnte nicht starten.", e.Message),
                ToolTipIcon.Error);
            return;
        }
        long micMs = feedback.ElapsedMilliseconds - beforeMic;
        Sounds.Start();
        _recordingSerial++;
        SetState(State.Recording);
        // One turn only: a Dictate run leaves the image for the next Dictate Prompt or template run.
        _runShot = mode != Mode.Dictate && Core.ScreenshotPolicy.Allowed(_settings) ? _heldShot.Take() : null;
        if (_runShot != null) Core.Log.Info($"{mode}: screenshot attached ({_runShot.Data.Length / 1024} KB)");
        _overlay.ShowRecording(mode, Hotkey.Display(stored), limit,
            mic != null ? () => mic.Level : null, mic != null ? () => mic.PeakSoFar : null,
            mode == Mode.Prompt ? _settings.QuickActions : null,
            templateName: mode == Mode.Template ? _template!.DisplayName : null, screenshot: _runShot != null,
            title: _live != null ? Core.UserCopy.LiveTitle : null);
        Core.Log.Info($"{mode}: overlay after {feedback.ElapsedMilliseconds} ms (setup {beforeMic} ms, microphone {micMs} ms)");
        if (mode == Mode.Dictate && _live == null && _welcomeForm is { TakesTryResult: true } tour && _pasteTarget == tour.Handle)
            tour.ShowTryListening();
        if (_selection != null)
        {
            var pending = _selection;
            _ = pending.ContinueWith(t =>
            {
                if (_selection == pending && t.IsCompletedSuccessfully) _overlay.SetSelection(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private Task StopAndProcessAsync() => _live != null ? ProcessLiveAsync(_audio!) : ProcessAsync(_audio!, null);

    // ---------------- live diktieren ----------------

    private void StartLiveDictation(string stored)
    {
        if (LiveUnavailable() is { } why)
        {
            PresentLiveUnavailable(why);
            return;
        }
        // The character already before the caret spaces and cases the first piece (Mac #328); when it cannot be read,
        // the end of the previous live dictation into the same program stands in for it (Mac #339).
        var front = ClipboardPaster.ForegroundRoot();
        _liveTargetApp = FocusedTextContext.ProcessName(front);
        var readCaret = FocusedTextContext.CharacterBeforeCaret(front);
        var caret = Core.LastInsertion.Seed(readCaret, _lastLive.Last, _liveTargetApp, DateTime.UtcNow);
        Core.Log.Info(caret != readCaret
            ? $"LIVE-DICTATE: caret context from last dictation ({caret.LogKind})"
            : $"LIVE-DICTATE: caret context {caret.LogKind}");
        _lastLive.Forget(null); // the new session records its own ending; the old memory is spent either way
        // Like Diktieren: "Nur gesprochen" applies offline, outside meetings (Live never runs in a meeting).
        _livePunctuation = Core.PunctuationModes.Effective(_settings.PunctuationMode, isOffline: true, duringMeeting: false);
        // The session exists before the recording starts, so a failed start finds it and discards it.
        var session = new Core.LiveDictationSession(
            (wav, ct) => DecodeLivePieceAsync(wav, ct),
            text => _paster.PasteLivePieceAsync(text, _liveTarget, CancellationToken.None),
            holdsPeriods: _livePunctuation != Core.PunctuationMode.SpokenOnly,
            caret: caret);
        _liveTarget = IntPtr.Zero;
        _livePieces.Clear();
        lock (_liveGate)
        {
            _liveChunker = new Core.MeetingChunker(16000, Core.LiveDictation.ChunkPolicy);
            _live = session;
        }
        StartRecording(Mode.Dictate, stored);
        if (_state != State.Recording)
        {
            DiscardLive(); // no clipboard change yet: BeginLive was not called
            return;
        }
        _liveTarget = _pasteTarget;
        _paster.BeginLive(_settings.RestoreClipboard);
        _liveRetryTimer.Start();
        var targetName = _liveTarget == IntPtr.Zero ? "none" : FocusedTextContext.ProcessName(_liveTarget) ?? "unknown";
        Core.Log.Info($"LIVE-DICTATE: started (target {targetName})");
        if (_liveTarget == IntPtr.Zero)
            Core.Log.Info("LIVE-DICTATE: no target window captured; pieces will be held");
        else if (NativeMethods.GetWindowThreadProcessId(_liveTarget, out uint targetPid) != 0 && targetPid == (uint)Environment.ProcessId)
            Core.Log.Info("LIVE-DICTATE: target is a window of this app; pieces will be held");
    }

    /// <summary>Capture thread: cuts the stream at pauses and hands finished pieces to the UI thread.</summary>
    private void FeedLive(Core.LiveDictationSession session, byte[] buffer, int count)
    {
        bool any = false;
        lock (_liveGate)
        {
            if (_live != session || _liveChunker == null) return;
            foreach (var chunk in _liveChunker.Write(MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, count & ~1))))
            {
                _livePieces.Enqueue((chunk, System.Diagnostics.Stopwatch.GetTimestamp()));
                any = true;
            }
        }
        if (any) _ui.Post(_ => DrainLivePieces(session), null);
    }

    /// <summary>UI thread: hands the finished pieces to the session, in order.</summary>
    private void DrainLivePieces(Core.LiveDictationSession session)
    {
        while (_live == session && _livePieces.TryDequeue(out var cut))
        {
            var chunk = cut.Chunk;
            var reason = chunk.DurationSeconds >= Core.LiveDictation.ChunkPolicy.MaxChunk.TotalSeconds - 0.01 ? "Max" : "Pause";
            session.Add(chunk.Wav, chunk.Silent, new Core.LivePieceInfo(reason, cut.Cut));
        }
    }

    private async Task<string> DecodeLivePieceAsync(byte[] wav, CancellationToken ct)
    {
        if (_fakeGemini) return "test";
        // No TextCleanup.ForDictation per piece: it would capitalise and end every piece with a period.
        var mode = _livePunctuation;
        if (mode != Core.PunctuationMode.SpokenOnly)
            return GlossaryBackstops(Core.LiveDictation.FinishPiece(await _offline.TranscribeAsync(wav, ct), mode));
        // "Nur gesprochen": Parakeet's marks stay only where the token timings show speech time behind them (Mac #339/#347).
        var (text, timed) = await _offline.TranscribeSpokenMarksAsync(wav, ct);
        return GlossaryBackstops(Core.LiveDictation.FinishPiece(text, mode, timedMarks: timed));
    }

    /// <summary>Forgets the live session. Does not touch the clipboard.</summary>
    private void DiscardLive(bool cancel = true)
    {
        _liveRetryTimer.Stop();
        var session = _live;
        lock (_liveGate)
        {
            _live = null;
            _liveChunker = null;
        }
        if (cancel) session?.Cancel();
        _livePieces.Clear();
    }

    private async Task ProcessLiveAsync(Core.IAudioSource audio)
    {
        var session = _live!;
        _audio = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _busyStatus = Core.UserCopy.LiveFinishing;
        SetState(State.Processing);
        _overlay.ShowProcessing(_busyStatus);
        Sounds.Stop();
        _unsupported = Array.Empty<string>();
        bool done = false;
        byte[]? wav = null;
        double seconds = 0;
        FailedRun? Failed() => wav == null ? null : new FailedRun(wav, Mode.Dictate, seconds, null, null, _liveTarget, StopKey: _stopKey, ClipboardOnly: true);
        var cancelledAction = LiveClipboardAtEnd(null, false);

        try
        {
            wav = await audio.StopAsync();
            seconds = Core.Wav.DurationSeconds(wav);
            Core.Log.Info($"Dictate (live): recorded {seconds:0.0} s");

            // A test file arrives whole: cut it like a recording would have been.
            if (audio is not MicAudioSource && Core.Wav.TryGetPcm16(wav, out var pcm) && pcm.SampleRate == 16000 && pcm.Channels == 1)
            {
                lock (_liveGate)
                {
                    if (_liveChunker != null)
                        foreach (var chunk in _liveChunker.Write(MemoryMarshal.Cast<byte, short>(wav.AsSpan(pcm.DataOffset, pcm.DataLength & ~1))))
                            _livePieces.Enqueue((chunk, System.Diagnostics.Stopwatch.GetTimestamp()));
                }
            }
            Core.MeetingAudioChunk? tail;
            lock (_liveGate)
            {
                tail = _liveChunker?.Flush();
                _liveChunker = null;
            }
            DrainLivePieces(session); // earlier pieces before the tail
            if (tail != null) session.Add(tail.Wav, tail.Silent, new Core.LivePieceInfo("Stop", System.Diagnostics.Stopwatch.GetTimestamp()));

            using var registration = ct.Register(session.Cancel);
            var outcome = await session.FinishAsync(wav);

            string fallback = "";
            if (outcome.FailedPieces > 0)
            {
                try
                {
                    fallback = Core.TextCleanup.ForDictation(GlossaryBackstops(await TranscribeOfflineAsync(wav, ct)));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Core.Log.Warn($"LIVE-DICTATE: whole-file fallback failed ({e.GetType().Name})");
                    fallback = "";
                }
            }

            var action = LiveClipboardAtEnd(outcome, fallback.Length > 0);
            _paster.EndLive(action, action switch
            {
                Core.LiveClipboardEnd.KeepHeld => outcome.HeldText,
                Core.LiveClipboardEnd.KeepFallback => fallback,
                _ => outcome.Text,
            });

            bool keepCheckpoint = false;
            string finalText = fallback.Length > 0 ? fallback : outcome.Text;
            if (action == Core.LiveClipboardEnd.KeepFallback)
            {
                Notify(Core.UserCopy.LiveTitle,
                    "Ein Teil des Diktats konnte nicht live eingefügt werden. Der vollständige Text liegt in der Zwischenablage.",
                    ToolTipIcon.Warning);
            }
            else if (outcome.FailedPieces > 0)
            {
                // Nothing to put on the clipboard: keep the take for a retry (and the crash checkpoint).
                keepCheckpoint = true;
                NotifyRetryable(Core.UserCopy.LiveTitle, "Ein Teil des Diktats konnte nicht erkannt werden. Bitte den Text prüfen.", Failed());
            }
            else if (outcome.HeldText.Length > 0)
            {
                Notify(Core.UserCopy.LiveTitle,
                    "Das Ende des Diktats wurde nicht eingefügt, weil ein anderes Fenster vorn war. Es liegt in der Zwischenablage: Strg+V drücken.",
                    ToolTipIcon.Info);
            }
            else if (outcome.Text.Length == 0)
            {
                Notify(Core.UserCopy.NoSpeechTitle, Core.UserCopy.NoSpeechText, ToolTipIcon.Info);
            }
            else
            {
                _overlay.ShowDone(Core.UserCopy.LiveInserted);
                done = true;
                _lastLive.Remember(outcome.Text, _liveTargetApp);
            }

            if (finalText.Length > 0) _recent.Add(finalText);
            if (outcome.FailedPieces == 0) _lastFailed = null;
            if (!keepCheckpoint) Core.RecordingCheckpoint.DiscardAll();

            if (outcome.FailedPieces == 0 && outcome.Text.Length > 0 && !_fakeGemini)
                _ = MeasureLiveDriftAsync(outcome.Text, wav);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Core.Log.Info("Dictate (live): cancelled by user");
            _paster.EndLive(cancelledAction, null);
            Core.RecordingCheckpoint.DiscardAll();
        }
        catch (Exception e) when (e is Core.AudioSilentException or MicrophoneUnavailableException)
        {
            Core.Log.Error($"Dictate (live): microphone problem {e.GetType().Name}");
            Sounds.Error();
            Notify("Problem mit dem Mikrofon", e.Message, ToolTipIcon.Warning);
            session.Cancel();
            _paster.EndLive(cancelledAction, null);
        }
        catch (Exception e)
        {
            Core.Log.Error("Dictate (live): unexpected error", e);
            Sounds.Error();
            NotifyRetryable(Core.UserCopy.ErrorTitle, "Etwas ist schiefgelaufen. Details stehen im Protokoll (Rechtsklick auf das Symbol → Protokoll öffnen).", Failed());
            session.Cancel();
            _paster.EndLive(cancelledAction, null);
        }
        finally
        {
            DiscardLive(cancel: false); // every path above has finished or cancelled the session; the clipboard is settled
            _cts.Dispose();
            _cts = null;
            if (!done) _overlay.HideOverlay();
            SetState(State.Idle);
        }
    }

    /// <summary>
    /// Accuracy measurement for the Mac's falsifier: how far the piecewise text is from one decode of the whole
    /// recording. Counts only, never words. Skipped when the app is busy again.
    /// </summary>
    private async Task MeasureLiveDriftAsync(string inserted, byte[] wav)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (_state != State.Idle)
            {
                Core.Log.Info("LIVE-DICTATE: drift measurement skipped, the app is busy");
                return;
            }
            var whole = await TranscribeOfflineAsync(wav, CancellationToken.None);
            var (insertedWords, wholeWords, distance) = Core.LiveDictation.Drift(inserted, whole);
            Core.Log.Info($"LIVE-DICTATE: drift insertedWords={insertedWords} wholeFileWords={wholeWords} wordEditDistance={distance}");
        }
        catch (Exception e)
        {
            Core.Log.Info($"LIVE-DICTATE: drift measurement failed ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// Tray menu or balloon click: run the last failed recording again. Like the Mac since 8.75 it only
    /// copies the result; it never pastes into the window the recording was made for (the window in front
    /// is whatever the user did since).
    /// </summary>
    private void RetryLast()
    {
        if (_state != State.Idle || _lastFailed == null) return;
        var run = _lastFailed;
        Core.Log.Info($"{run.Mode}: retrying the last recording ({run.Seconds:0.0} s)");
        RunAgain(run);
    }

    // Mac: „Beschäftigt“ when a menu row needs the pipeline while a recording or a run is going.
    private bool NotIdleSayBusy()
    {
        if (_state == State.Idle) return false;
        Notify("Beschäftigt", "Beende zuerst die laufende Aufnahme und versuch es dann erneut.", ToolTipIcon.Info);
        return true;
    }

    /// <summary>
    /// Tray „Abgebrochene Aufnahme transkribieren“ (Mac transcribeCancelledRecording): the dictation cancelled while it was
    /// processed runs again. Started from idle, so the result is copied, never pasted.
    /// </summary>
    private void TranscribeCancelledRecording()
    {
        if (NotIdleSayBusy()) return;
        if (_cancelled.Take() is not { } kept) return;
        Core.Log.Info($"Dictate: transcribing the cancelled recording ({kept.Seconds:0.0} s)");
        RunAgain(new FailedRun(kept.Wav, Mode.Dictate, kept.Seconds, null, null, IntPtr.Zero,
            StopKey: _settings.DictateHotkey, ClipboardOnly: true));
    }

    /// <summary>
    /// Tray „Nochmal ohne Vorlage“ (Mac runAgainWithoutTemplate): the last template dictation's audio goes through
    /// Dictate Prompt with its own system prompt, no template and no selection, and the result is only copied.
    /// </summary>
    private void RunAgainWithoutTemplate()
    {
        if (NotIdleSayBusy()) return;
        if (_templateReplay.Current(DateTime.UtcNow) is not { } kept) return;
        Core.Log.Info($"Prompt: running the last template dictation again without its template ({kept.Seconds:0.0} s)");
        RunAgain(new FailedRun(kept.Wav, Mode.Prompt, kept.Seconds, null, null, IntPtr.Zero,
            StopKey: _settings.PromptHotkey, ClipboardOnly: true, NoNewTextTemplate: true));
    }

    /// <summary>Runs kept audio through the pipeline from idle (retry, cancelled recording, without template).</summary>
    private void RunAgain(FailedRun run)
    {
        _mode = run.Mode;
        _template = run.Template;
        _stopKey = run.StopKey;
        _quickAction = run.QuickAction;
        _noNewTextTemplate = run.NoNewTextTemplate;
        _runShot = Core.ScreenshotPolicy.Allowed(_settings) ? run.Screenshot : null; // switched off or offline since
        _paster.CancelLateWatch(); // a flag from the old recording must not block this run
        _pasteTarget = run.Target;
        _ = ProcessAsync(null, run);
    }

    /// <param name="audio">The running recording to stop, or null for a retry of <paramref name="retry"/>.</param>
    private async Task ProcessAsync(Core.IAudioSource? audio, FailedRun? retry)
    {
        _audio = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _busyStatus = _quickAction != null ? Core.UserCopy.QuickActionBusy(_quickAction)
            : _mode == Mode.Dictate ? Core.UserCopy.Transcribing
            : _mode == Mode.Template && _template != null ? Core.UserCopy.TemplateBusy(_template.DisplayName)
            : Core.UserCopy.RecognizingInstruction;
        SetState(State.Processing);
        _overlay.ShowProcessing(_busyStatus);
        Sounds.Stop();
        _unsupported = Array.Empty<string>();
        bool done = false;
        byte[]? wav = null;
        double seconds = 0;
        string? selection = null;
        // What a retry needs; null when the audio itself is lost (then there is nothing to try again).
        FailedRun? Failed() => wav == null ? null : new FailedRun(wav, _mode, seconds, selection, _quickAction, _pasteTarget, _template, _stopKey, Screenshot: _runShot, NoNewTextTemplate: _noNewTextTemplate);

        try
        {
            if (retry != null)
            {
                (wav, seconds, selection) = (retry.Wav, retry.Seconds, retry.Selection);
            }
            else
            {
                try
                {
                    wav = await audio!.StopAsync();
                }
                catch (Core.AudioSilentException) when (_quickAction != null || _mode == Mode.Template)
                {
                    // Text marked, Ctrl+1 pressed without a word (or a template run on a selection): the selection alone is the input.
                    // A silent template run with nothing marked is still a microphone problem.
                    if (_quickAction == null && _selection != null) selection = await _selection;
                    if (_quickAction == null && !Core.TemplatePrompt.AcceptsEmptyTranscript(selection)) throw;
                    wav = Array.Empty<byte>();
                }
                seconds = wav.Length == 0 ? 0 : Core.Wav.DurationSeconds(wav);
                Core.Log.Info($"{_mode}: recorded {seconds:0.0} s");
                // A quick instruction needs no speech: mark text, press the hotkey and right away Ctrl+1.
                // A template may format a selection with no speech, so it needs the selection before it can judge "too short".
                if (_mode == Mode.Template && _selection != null && selection == null) selection = await _selection;
                if (seconds < 0.5 && _quickAction == null
                    && !(_mode == Mode.Template && Core.TemplatePrompt.AcceptsEmptyTranscript(selection)))
                {
                    // A double press, not a recording: say so instead of a misleading microphone error.
                    Core.RecordingCheckpoint.DiscardAll();
                    Notify("Aufnahme zu kurz", "Kürzel drücken, sprechen, dann erneut drücken.", ToolTipIcon.Info);
                    return;
                }
                if (_mode == Mode.Prompt && _selection != null) selection = await _selection;
            }

            string text = _mode switch
            {
                Mode.Dictate => await DictateAsync(wav, seconds, ct),
                Mode.Template => await TemplateAsync(wav, seconds, selection, retry != null, ct),
                _ => await PromptAsync(wav, seconds, selection, retry != null, ct),
            };
            ct.ThrowIfCancellationRequested();

            if (text.Length == 0)
            {
                Core.RecordingCheckpoint.DiscardAll();
                if (_mode == Mode.Dictate && _welcomeForm is { TakesTryResult: true } silentTour && _pasteTarget == silentTour.Handle) silentTour.ShowTryResult("");
                return; // already reported
            }
            _lastFailed = null; // this run worked; the old failure is history
            Core.RecordingCheckpoint.DiscardAll();
            _recent.Add(text);
            // Mac: a template shaped this dictation; for 10 minutes the same audio can run without it.
            if (_mode == Mode.Template && wav is { Length: > 0 } && _template != null)
            {
                _templateReplay.Keep(wav, seconds, _template.DisplayName, DateTime.UtcNow);
                _replayExpiry.Start();
            }

            // The tour's „Ausprobieren“ step started this dictation in its own window: show the text there.
            if (_mode == Mode.Dictate && retry == null && _welcomeForm is { TakesTryResult: true } tour
                && _pasteTarget != IntPtr.Zero && _pasteTarget == tour.Handle)
            {
                _paster.KeepOnClipboard(text);
                tour.ShowTryResult(text);
                _overlay.ShowDone("Es funktioniert");
                done = true;
                return;
            }

            // A quick-action rewrite that lost most of a long selection must not silently replace it. Spoken
            // instructions are unknown here (Gemini hears them) and "fass zusammen" is a normal request, so
            // they rely on the MAX_TOKENS check alone.
            if (_mode == Mode.Prompt && selection != null && _quickAction != null
                && Core.ReplyGuards.AnswerSuspiciouslyShort(selection.Length, text.Length, _quickAction))
            {
                Core.Log.Info($"Prompt: answer {text.Length} chars for a selection of {selection.Length}, not pasted");
                if (_paster.KeepOnClipboard(text))
                {
                    Sounds.Error();
                    Notify("Bitte prüfen", "Antwort deutlich kürzer als die Markierung – liegt in der Zwischenablage, bitte prüfen.", ToolTipIcon.Warning);
                }
                else
                {
                    Sounds.Error();
                    Notify("Einfügen fehlgeschlagen", "Die Zwischenablage war blockiert. Bitte noch einmal versuchen.", ToolTipIcon.Warning);
                }
                return;
            }

            // Like the Mac since 8.75: a retry runs from idle (tray menu or balloon click, recovered recording);
            // the window in front then is whatever the user did since, so copy only.
            var keepOnly = !Core.ClipboardPasteGuard.MayAutoPaste(_settings.AutoPaste, clipboardOnlyRun: retry != null);
            var outcome = keepOnly
                ? (_paster.KeepOnClipboard(text) ? PasteOutcome.ClipboardOnlyByChoice : PasteOutcome.Failed)
                : await _paster.PasteAsync(text, _pasteTarget, _settings.ClipboardAfterPaste, ct);
            Core.Log.Info($"{_mode}: {outcome}, {text.Length} chars, autoPaste {(_settings.AutoPaste ? "on" : "off")}, after paste {Core.ClipboardAfterPasteModes.Name(_settings.ClipboardAfterPaste)}");
            switch (outcome)
            {
                case PasteOutcome.ClipboardOnlyByChoice when _unsupported.Count > 0:
                    _overlay.ShowWarning(_unsupported, pasted: false);
                    done = true;
                    break;
                case PasteOutcome.ClipboardOnlyByChoice:
                    _overlay.ShowDone("In der Zwischenablage");
                    done = true;
                    break;
                case PasteOutcome.Pasted when _unsupported.Count > 0:
                    _overlay.ShowWarning(_unsupported, pasted: true);
                    done = true;
                    break;
                case PasteOutcome.Pasted:
                    _overlay.ShowDone("Eingefügt");
                    done = true;
                    break;
                case PasteOutcome.ClipboardOnlyElevatedTarget or PasteOutcome.ClipboardOnlyKeysHeld or PasteOutcome.ClipboardOnlyTargetChanged
                    or PasteOutcome.ClipboardOnlyOwnWindow or PasteOutcome.ClipboardOnlyTargetGone
                    or PasteOutcome.ClipboardOnlyClipboardChanged when _unsupported.Count > 0:
                    // The balloon would bury the warning; the card says both.
                    _overlay.ShowWarning(_unsupported, pasted: false);
                    done = true;
                    break;
                case PasteOutcome.ClipboardOnlyElevatedTarget:
                    Notify("Bitte selbst einfügen",
                        "Das Zielprogramm läuft als Administrator, dorthin darf WhisperShortcut nicht tippen. Der Text liegt in der Zwischenablage: Strg+V drücken.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.ClipboardOnlyKeysHeld:
                    Notify("Bitte selbst einfügen",
                        "Strg, Umschalt oder Alt war noch gedrückt. Der Text liegt in der Zwischenablage: Strg+V drücken.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.ClipboardOnlyTargetChanged:
                    Notify("Bitte selbst einfügen",
                        "Du hast das Fenster gewechselt. Der Text liegt in der Zwischenablage: Strg+V drücken.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.ClipboardOnlyTargetGone:
                    Notify("Bitte selbst einfügen",
                        "Das Fenster, in dem die Aufnahme begann, ist geschlossen. Der Text liegt in der Zwischenablage: Strg+V drücken.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.ClipboardOnlyClipboardChanged:
                    Notify("Nicht eingefügt",
                        "Die Zwischenablage hat sich kurz vor dem Einfügen geändert. Den Text gibt es im Menü am Symbol unter „Letzte Transkriptionen“.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.ClipboardOnlyOwnWindow:
                    Notify("Bitte selbst einfügen",
                        "Vorn war ein Fenster von WhisperShortcut. Der Text liegt in der Zwischenablage: Strg+V drücken.",
                        ToolTipIcon.Info);
                    break;
                case PasteOutcome.Failed:
                    Sounds.Error();
                    Notify("Einfügen fehlgeschlagen", "Die Zwischenablage war blockiert. Bitte noch einmal versuchen.", ToolTipIcon.Warning);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The pill disappearing is the feedback; a balloon on top of it would be noise.
            Core.Log.Info($"{_mode}: cancelled by user");
            // Mac: a cancel is one keystroke and can be an accident; keep a dictation for the tray menu (only dictation:
            // an instruction run again as dictation would paste the instruction).
            if (_mode == Mode.Dictate && wav is { Length: > 0 })
            {
                _cancelled.Keep(wav, seconds);
                Core.Log.Info($"Dictate: kept the cancelled recording ({seconds:0.0} s) for the tray menu");
            }
            Core.RecordingCheckpoint.DiscardAll();
        }
        catch (SelectionUnreadableException)
        {
            // Writing a new text now would overwrite the marked one.
            Sounds.Error();
            Notify("Nichts geändert", "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", ToolTipIcon.Warning);
        }
        catch (OfflineNotReadyException e)
        {
            Core.Log.Error($"{_mode}: offline model missing");
            Sounds.Error();
            Notify(Core.UserCopy.ModelNotDownloadedTitle, e.Message, ToolTipIcon.Warning, ShowSettings, Core.UserCopy.ActionOpenSettings);
        }
        catch (Core.LocalLlmException e)
        {
            Core.Log.Error($"{_mode}: local LLM failed", e);
            Sounds.Error();
            NotifyRetryable(Core.UserCopy.OfflineModelErrorTitle, e.Message, Failed());
        }
        catch (Core.EmptyModelReplyException e)
        {
            Core.Log.Error($"{_mode}: empty model reply");
            Sounds.Error();
            NotifyRetryable(Core.UserCopy.NoReplyTitle, e.Message, Failed());
        }
        catch (Core.GeminiException e)
        {
            Core.Log.Error($"{_mode}: Gemini {e.Kind}");
            Sounds.Error();
            if (e.Kind == Core.GeminiErrorKind.Billing)
            {
                // Retrying cannot fill an empty account, but the audio stays for "Letzte Aufnahme erneut verarbeiten" after topping up.
                _lastFailed = Failed();
                var url = e.BillingUrl;
                Notify(Core.UserCopy.ProviderErrorTitle(e.Kind, e.Provider, e.Message), e.Message, ToolTipIcon.Error,
                    url == null ? ShowSettings : () => OpenBillingUrl(url), url == null ? Core.UserCopy.ActionOpenSettings : Core.UserCopy.ActionTopUp);
            }
            else if (e.Kind is Core.GeminiErrorKind.NoKey or Core.GeminiErrorKind.InvalidKey)
                Notify(Core.UserCopy.ProviderErrorTitle(e.Kind, e.Provider, e.Message), e.Message, ToolTipIcon.Error,
                    () => ShowSettingsPage(SettingsForm.AccountsPage), Core.UserCopy.ActionOpenSettings);
            else if (e.Kind is Core.GeminiErrorKind.Network or Core.GeminiErrorKind.Timeout or Core.GeminiErrorKind.Quota or Core.GeminiErrorKind.Server)
                NotifyRetryable(Core.UserCopy.ProviderErrorTitle(e.Kind, e.Provider, e.Message), e.Message, Failed());
            else
                Notify(Core.UserCopy.ProviderErrorTitle(e.Kind, e.Provider, e.Message), e.Message, ToolTipIcon.Error);
        }
        catch (Exception e) when (e is Core.AudioSilentException or MicrophoneUnavailableException)
        {
            Core.Log.Error($"{_mode}: microphone problem {e.GetType().Name}");
            Sounds.Error();
            Notify("Problem mit dem Mikrofon", e.Message, ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            Core.Log.Error($"{_mode}: unexpected error", e);
            Sounds.Error();
            NotifyRetryable("Fehler", "Etwas ist schiefgelaufen. Details stehen im Protokoll (Rechtsklick auf das Symbol → Protokoll öffnen).", Failed());
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _selectionCts?.Cancel(); // an early end must stop the selection copy still polling
            _selectionCts?.Dispose();
            _selectionCts = null;
            _selection = null;
            _quickAction = null;
            _noNewTextTemplate = false;
            _runShot = null; // a failed run keeps it in _lastFailed for the retry
            if (!done) _overlay.HideOverlay();
            SetState(State.Idle);
        }
    }

    /// <summary>Opens the provider's billing page from the "Guthaben" balloon.</summary>
    private static void OpenBillingUrl(string url)
    {
        if (!Core.NetworkPolicy.IsAllowedBrowserUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening the billing page failed: {e.GetType().Name}"); }
    }

    /// <summary>
    /// Error balloon that, when the audio is still there, keeps the run for a retry and offers it on click.
    /// </summary>
    private void NotifyRetryable(string title, string message, FailedRun? run)
    {
        if (run == null)
        {
            Notify(title, message, ToolTipIcon.Error);
            return;
        }
        _lastFailed = run;
        Notify(title, message, ToolTipIcon.Error, () => RetryLast(), Core.UserCopy.ActionRetry);
    }

    private async Task<string> DictateAsync(byte[] wav, double seconds, CancellationToken ct)
    {
        var punctuation = Core.PunctuationModes.Effective(_settings.PunctuationMode,
            isOffline: _settings.EffectiveSpeechEngine == Core.Engine.Local, duringMeeting: false);
        string raw = _fakeGemini
            ? "das ist ein test diktat aus der virtuellen maschine"
            : await TranscribeAsync(wav, ct, punctuation);
        raw = GlossaryBackstops(raw);
        string text = Core.TextCleanup.DiscardImplausible(
            Core.TextCleanup.ForDictation(raw, addTrailingPeriod: punctuation == Core.PunctuationMode.Automatic), seconds);
        // A chatbot answer ("please provide the audio") or our own instruction is no dictation.
        if (Core.TextCleanup.LooksLikeRefusal(text)) text = "";
        Core.Log.Info($"Dictate: {raw.Length} chars raw, {text.Length} chars after cleanup, punctuation {punctuation}");
        if (text.Length == 0) Notify(Core.UserCopy.NoSpeechTitle, Core.UserCopy.NoSpeechText, ToolTipIcon.Info);
        return text;
    }

    private async Task<string> PromptAsync(byte[] wav, double seconds, string? selection, bool isRetry, CancellationToken ct)
    {
        // A Ctrl+C that came after the 2 s deadline means there is a selection we never read: writing a
        // new text now would replace it. (A retry has its own, finished selection.)
        if (selection == null && !isRetry && _paster.LateCopyArrived)
        {
            Core.Log.Info("Prompt: selection arrived after the deadline, not composing over it");
            throw new SelectionUnreadableException();
        }
        Core.Log.Info($"Prompt: {(selection == null ? "no selection (compose)" : $"selection {selection.Length} chars")}");

        string systemPrompt = Core.DictatePromptText.BuildSystemPrompt(_settings.PromptModeSystemPrompt,
            _noNewTextTemplate ? null : _settings.DictatePromptTemplate) ?? "";
        if (_quickAction != null) return await QuickActionAsync(_quickAction, wav, seconds, selection, systemPrompt, ct);

        if (_fakeGemini)
        {
            return selection == null
                ? "Angebot (Testmodus ohne Gemini)"
                : $"Bearbeitet (Testmodus ohne Gemini): {selection.Length} Zeichen markiert";
        }

        // Only Gemini hears the instruction itself, and only up to the Dictate Prompt limit (inline request); a local or
        // OpenAI text model, or a longer take (a template recording replayed without its template), gets it transcribed.
        // +1 s: a recording stopped by the limit can run a buffer past it and must still go inline.
        if (Core.DictatePromptText.UsesTranscript(_settings.EffectiveTextEngine, seconds, MicAudioSource.PromptLimit.TotalSeconds + 1))
            return await PromptFromTranscriptAsync(wav, selection, systemPrompt, ct);
        if (!HasSpeech(wav))
        {
            Notify(Core.UserCopy.NoSpeechTitle, "In der Aufnahme war keine Anweisung zu hören. Sprich sie noch einmal.", ToolTipIcon.Info);
            return "";
        }
        SetBusy(Core.UserCopy.EditingText);
        var answer = _gemini.PromptAsync(wav, selection, systemPrompt, ct, HistoryFor(selection), _runShot);

        string? instruction = null;
        if (selection == null)
        {
            // Nothing selected and the instruction is only "korrigiere" / "mach das freundlicher":
            // there is nothing to edit, and the model would invent a text. Transcribe in parallel to check.
            instruction = await TranscribeAsync(wav, ct);
            if (Core.BareEditCommand.Matches(instruction))
            {
                Core.Log.Info("Prompt: bare edit command without selection, refused");
                _ = answer.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe, discard
                Sounds.Error();
                Notify(Core.UserCopy.NothingSelectedTitle, Core.UserCopy.NothingSelectedText, ToolTipIcon.Info);
                return "";
            }
        }

        // With a selection Gemini hears the instruction itself; there is no transcript to compare with.
        string text = Reply(await answer, instruction == null ? null : new[] { instruction, systemPrompt });
        Core.Log.Info($"Prompt: answer {text.Length} chars");
        text = Core.ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
        RememberPrompt(selection, instruction ?? Core.PromptConversationMemory.SpokenPlaceholder, text);
        return text;
    }

    /// <summary>
    /// A dictation template run. The recording is transcribed in chunks (so a long dictation is not cut off) and the
    /// transcript goes as text to the text engine, with the template's instructions as the whole system prompt.
    /// A selection is formatted into the template; without one the dictation is.
    /// </summary>
    private async Task<string> TemplateAsync(byte[] wav, double seconds, string? selection, bool isRetry, CancellationToken ct)
    {
        var template = _template ?? throw new InvalidOperationException("Template run without a template");
        // Same guard as Dictate Prompt: a late Ctrl+C means a selection we never read.
        if (selection == null && !isRetry && _paster.LateCopyArrived)
        {
            Core.Log.Info("Template: selection arrived after the deadline, not composing over it");
            throw new SelectionUnreadableException();
        }

        if (wav.Length > 0 && seconds >= 1.0) SetBusy(Core.UserCopy.Transcribing);
        string transcript = wav.Length == 0 || seconds < 1.0 ? ""
            : _fakeGemini ? "Patient klagt über Kopfschmerzen seit drei Tagen"
            // Parakeet gets the glossary as hotwords, but not the echo strip (no prompt to echo); rejected and term spellings still apply.
            : GlossaryBackstops(await TranscribeAsync(wav, ct), echoStrip: _settings.EffectiveSpeechEngine != Core.Engine.Local);
        transcript = transcript.Trim();
        if (Core.TextCleanup.LooksLikeRefusal(transcript)) transcript = "";
        Core.Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars");
        if (transcript.Length == 0 && !Core.TemplatePrompt.AcceptsEmptyTranscript(selection))
        {
            Notify(Core.UserCopy.NoSpeechTitle, Core.UserCopy.NoSpeechText, ToolTipIcon.Info);
            return "";
        }

        // Nothing from Dictate Prompt's stored prompt, the template for new texts or the quick instructions.
        string system = Core.TemplatePrompt.SystemPrompt(template.Instructions);
        string user = Core.TemplatePrompt.UserTurn(selection, transcript);

        SetBusy(Core.UserCopy.TemplateBusy(template.DisplayName));
        var sw = Stopwatch.StartNew();
        string raw;
        if (_fakeGemini)
        {
            raw = "Anamnese: Kopfschmerzen seit drei Tagen\nBefund: unauffällig\nTherapie: Ibuprofen 400 mg";
        }
        else
        {
            switch (_settings.EffectiveTextEngine)
            {
                case Core.Engine.Local:
                    if (!_offline.LlmReady) SetBusy(Core.UserCopy.LoadingLlm);
                    raw = await _offline.CompleteAsync(system, user, Core.LocalLlmClient.TemplateMaxTokens, ct);
                    break;
                case Core.Engine.OpenAI:
                    raw = await _openAi.CompleteTextAsync(system, user, _runShot, ct);
                    break;
                default:
                    raw = await _gemini.CompleteTextAsync(system, user, _runShot, ct);
                    break;
            }
        }
        string text = Reply(raw, new[] { transcript, selection }, template.Instructions, keepMarkdown: true);
        Core.Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars, answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        text = Core.ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
        return text;
    }

    /// <summary>
    /// Dictate Prompt with a text model that cannot hear (local, as on the Mac, or OpenAI): the
    /// instruction is transcribed with the chosen speech engine and sent as text with the selection.
    /// </summary>
    private async Task<string> PromptFromTranscriptAsync(byte[] wav, string? selection, string systemPrompt, CancellationToken ct)
    {
        SetBusy(Core.UserCopy.Transcribing);
        string instruction = (await TranscribeAsync(wav, ct)).Trim();
        Core.Log.Info($"Prompt via transcript ({_settings.EffectiveTextEngine}): instruction {instruction.Length} chars");
        if (instruction.Length == 0)
        {
            Notify(Core.UserCopy.NoSpeechTitle, "In der Aufnahme war keine Anweisung zu hören. Sprich sie noch einmal.", ToolTipIcon.Info);
            return "";
        }
        if (selection == null && Core.BareEditCommand.Matches(instruction))
        {
            Core.Log.Info("Prompt: bare edit command without selection, refused");
            Sounds.Error();
            Notify(Core.UserCopy.NothingSelectedTitle, Core.UserCopy.NothingSelectedText, ToolTipIcon.Info);
            return "";
        }

        SetBusy(Core.UserCopy.EditingText);
        var sw = Stopwatch.StartNew();
        string text = Reply(await PromptTextAsync(selection, instruction, systemPrompt, ct), new[] { instruction, selection, systemPrompt });
        Core.Log.Info($"Prompt via transcript: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        text = Core.ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
        RememberPrompt(selection, instruction, text);
        return text;
    }

    /// <summary>
    /// A quick instruction (Ctrl+1–9 or a click while recording). Whatever was said is transcribed and
    /// goes along as text, so this path always has a transcript to check the reply against.
    /// </summary>
    private async Task<string> QuickActionAsync(string quick, byte[] wav, double seconds, string? selection, string systemPrompt, CancellationToken ct)
    {
        // Under a second nothing was said yet; a recognizer would only guess at the silence.
        if (seconds >= 1.0) SetBusy(Core.UserCopy.Transcribing);
        string transcript = seconds < 1.0 ? ""
            : _fakeGemini ? "Patient klagt über Kopfschmerzen seit drei Tagen"
            // Parakeet gets the glossary as hotwords, but not the echo strip (no prompt to echo); rejected and term spellings still apply.
            : GlossaryBackstops(await TranscribeAsync(wav, ct), echoStrip: _settings.EffectiveSpeechEngine != Core.Engine.Local);
        var request = Core.QuickActionRequest.Build(quick, selection, transcript);
        Core.Log.Info($"Prompt quick action: transcript {transcript.Trim().Length} chars, selection {selection?.Length ?? 0} chars");
        if (request == null)
        {
            Sounds.Error();
            Notify("Nichts zum Anwenden", $"Erst Text markieren oder etwas diktieren, dann {Core.QuickActionList.KeyRange} drücken.", ToolTipIcon.Info);
            return "";
        }
        var (target, instruction, fromDictation) = request.Value;
        SetBusy(Core.UserCopy.QuickActionBusy(quick));

        string raw;
        var sw = Stopwatch.StartNew();
        if (_fakeGemini)
        {
            // Test mode: Markdown and an invented drug, so the plain-text cleanup and the warning show.
            raw = "## Anamnese\nKopfschmerzen seit drei Tagen\n\n**Therapie:**\n* Ibuprofen 400 mg";
        }
        else
        {
            raw = await PromptTextAsync(target, instruction, systemPrompt, ct, compose: fromDictation);
        }
        string text = Reply(raw, new[] { transcript, selection, quick, systemPrompt });
        Core.Log.Info($"Prompt quick action: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        text = Core.ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
        // A dictated target is a compose turn: remembered without a selection, like the Mac.
        RememberPrompt(fromDictation ? null : target, instruction, text);
        return text;
    }

    /// <summary>
    /// Cleans a language-model reply for a plain text field and, when there is a transcript to compare
    /// with, collects the words nobody dictated (<see cref="_unsupported"/>, shown after the paste).
    /// The template text is NOT a source: example content in it (a drug, a dose) must still be flagged when
    /// nobody dictated it. Only a template's label words pass (<paramref name="templateInstructions"/>).
    /// A template run keeps its Markdown (<paramref name="keepMarkdown"/>), as on the Mac.
    /// </summary>
    private string Reply(string raw, string?[]? sources, string? templateInstructions = null, bool keepMarkdown = false)
    {
        string text = Core.TextCleanup.NormalizeReply(raw);
        if (!keepMarkdown) text = Core.TextCleanup.StripMarkdown(text);
        if (sources == null || text.Length == 0) return text;
        _unsupported = Core.DictationFidelityCheck.UnsupportedWords(text, sources, templateInstructions);
        // Count only: the words are patient data and stay out of the log.
        if (_unsupported.Count > 0) Core.Log.Info($"Prompt: {_unsupported.Count} word(s) in the reply are not in the dictation, warning shown");
        return text;
    }

    /// <summary>Speech recognition with the chosen engine: Parakeet on this PC, or Gemini / OpenAI.</summary>
    private Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, Core.PunctuationMode punctuation = Core.PunctuationMode.Automatic,
        bool meetingChunk = false) =>
        _settings.EffectiveSpeechEngine switch
        {
            Core.Engine.Local => TranscribeOfflineAsync(wav, ct, punctuation),
            Core.Engine.OpenAI => TranscribeOnlineAsync(_openAi, wav, ct, meetingChunk),
            _ => TranscribeOnlineAsync(_gemini, wav, ct, meetingChunk),
        };

    /// <summary>
    /// Earlier online edit turns to send with this request. Compose and Offline Mode send none
    /// (Mac SpeechService.includesPromptHistory).
    /// </summary>
    private IReadOnlyList<Core.PromptTurn>? HistoryFor(string? selection)
    {
        if (!Core.PromptConversationMemory.Includes(isCompose: string.IsNullOrEmpty(selection), offline: _settings.OfflineMode))
            return null;
        var turns = _promptMemory.TurnsForApi();
        return turns.Count == 0 ? null : turns;
    }

    /// <summary>Keeps a finished turn so "jetzt noch kürzer" still sees the previous edit.</summary>
    private void RememberPrompt(string? selection, string instruction, string response)
    {
        if (!Core.PromptConversationMemory.Records(_settings.OfflineMode)) return;
        _promptMemory.Append(selection, instruction, response);
    }

    /// <summary>A text-only Dictate Prompt call with the chosen text engine.</summary>
    private Task<string> PromptTextAsync(string? selection, string instruction, string systemPrompt, CancellationToken ct, bool compose = false)
    {
        // compose: the target is the fresh transcript, not a selection, so no earlier turns go along.
        var history = compose ? null : HistoryFor(selection);
        switch (_settings.EffectiveTextEngine)
        {
            case Core.Engine.Local:
                if (!_offline.LlmReady)
                    SetBusy(Core.UserCopy.LoadingLlm);
                return _offline.PromptAsync(selection, instruction, systemPrompt, ct);
            case Core.Engine.OpenAI:
                return _openAi.PromptTextAsync(selection, instruction, systemPrompt, ct, history, _runShot);
            default:
                return _gemini.PromptTextAsync(selection, instruction, systemPrompt, ct, history, _runShot);
        }
    }

    /// <summary>
    /// Gemini or OpenAI. Gemini is cut above 2.5 min, OpenAI above 11 min (25 MB upload limit).
    /// Pieces are cut in pauses, four at a time: one request would hit Gemini's inline size limit
    /// and take as long as all of it. A piece that comes back as nothing but glossary words (the
    /// model filled a quiet stretch with vocabulary) is asked again once without the glossary, like the Mac.
    /// </summary>
    private Task<string> TranscribeOnlineAsync(Core.ISpeechProvider speech, byte[] wav, CancellationToken ct, bool meetingChunk = false)
    {
        var glossary = _settings.Glossary.ToList();
        var echoTerms = Core.GlossaryPrompt.EchoTerms(glossary);
        return Core.ChunkedTranscription.TranscribeAsync(
            speech is Core.OpenAiClient ? Core.WavChunker.SplitForOpenAi(wav) : Core.WavChunker.Split(wav),
            async (chunk, c) =>
            {
                // Nothing but room noise: Gemini would answer with the glossary. Also saves the request.
                if (!HasSpeech(chunk)) return "";
                // Live-meeting chunks carry loud system audio that gpt-transcribe answers with the keywords themselves.
                var text = speech is Core.OpenAiClient oa
                    ? await oa.TranscribeAsync(chunk, glossary, sendKeywords: !meetingChunk, c)
                    : await speech.TranscribeAsync(chunk, glossary, c);
                if (text.Trim().Length == 0
                    || Core.TextCleanup.DiscardGlossaryEcho(text, Core.Wav.DurationSeconds(chunk), echoTerms).Length > 0)
                    return text;
                Core.Log.Info("Dictate: glossary echo discarded, asking again without the glossary");
                return await speech.TranscribeAsync(chunk, Array.Empty<string>(), c);
            }, maxParallel: 4, ct);
    }

    /// <summary>Speech gate for a recording or chunk going to Gemini or OpenAI; the file source of --test-wav passes through here too.</summary>
    private static bool HasSpeech(byte[] wav)
    {
        var level = Core.SpeechGate.Analyze(wav);
        if (Core.SpeechGate.HasSpeech(level)) return true;
        Core.Log.Info($"No speech (peak {level.PeakDbfs:0} dBFS, loud frames {level.LoudFrames}/{level.Frames}), request skipped");
        return false;
    }

    /// <summary>
    /// Runs on every transcript, online and offline, as on the Mac: a glossary list tacked onto the end
    /// is cut off, a spelling the user rejected (<c>Claude CLI (not "Cloud CLI")</c>) is corrected, and a
    /// name Gemini wrote the common way ("Gödde" for "goedde") is set to the glossary spelling.
    /// </summary>
    private string GlossaryBackstops(string raw, bool echoStrip = true)
    {
        var glossary = _settings.Glossary.ToList();
        // Parakeet is given no prompt it could echo, so its text skips the strip (Mac: offline template input only gets the spelling fixes).
        var stripped = echoStrip ? Core.TextCleanup.StripTrailingGlossaryEcho(raw, Core.GlossaryPrompt.EchoTerms(glossary)) : raw;
        var corrected = Core.GlossaryPrompt.CorrectRejectedSpellings(stripped, glossary);
        return Core.GlossaryPrompt.ApplyTermSpellings(corrected, Core.GlossaryPrompt.EchoTerms(glossary));
    }

    /// <summary>
    /// Parakeet, in 30-second pieces above 40 s, one at a time (the recognizer is serialized anyway).
    /// Spoken "Doppelpunkt" / "neuer Absatz" become punctuation; Gemini does that itself.
    /// </summary>
    private async Task<string> TranscribeOfflineAsync(byte[] wav, CancellationToken ct,
        Core.PunctuationMode punctuation = Core.PunctuationMode.Automatic)
    {
        if (punctuation != Core.PunctuationMode.SpokenOnly)
        {
            var joined = await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
                (chunk, c) => _offline.TranscribeAsync(chunk, c), maxParallel: 1, ct);
            return Core.SpokenPunctuation.Apply(joined, punctuation);
        }
        // "Nur gesprochen" (Mac #339/#347): each piece keeps Parakeet's marks only where the token timings show speech
        // time behind them; a piece without usable timings falls back to the plain strip. Then, as on the Mac, the
        // joined text loses only semicolons (timedMarks) before the spoken commands are applied.
        var spoken = await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
            async (chunk, c) =>
            {
                var (text, timed) = await _offline.TranscribeSpokenMarksAsync(chunk, c);
                return timed ? text : Core.SpokenPunctuation.StripModelPunctuation(text);
            }, maxParallel: 1, ct);
        return Core.SpokenPunctuation.Apply(Core.SpokenPunctuation.StripModelPunctuation(spoken, timedMarks: true), punctuation);
    }

    // ---------------- live meeting ----------------

    /// <summary>
    /// Why a meeting cannot start (or its backlog cannot be retried) with the current engines, or null.
    /// Offline works: Parakeet transcribes meeting chunks like long dictations.
    /// </summary>
    private string? MeetingUnavailable()
    {
        if (_fakeGemini) return null;
        if (_settings.EffectiveMeetingSpeechEngine == Core.Engine.Local && !OfflineEngine.SpeechInstalled)
            return "Die Offline-Spracherkennung ist noch nicht geladen. In den Einstellungen unter „Offline-Modelle“ laden.";
        if (MeetingMissingKey() is { } provider) // TrayApp.Meetings.cs
            return $"Noch kein {provider}-Schlüssel eingetragen. Bitte zuerst in den Einstellungen eintragen.";
        return null;
    }

    /// <summary>
    /// One meeting chunk through the dictation path: same engine, glossary and cleanup, but no paste.
    /// Runs on a worker thread of the meeting session; touches no UI.
    /// </summary>
    private async Task<string> TranscribeMeetingChunkAsync(byte[] wav, CancellationToken ct)
    {
        if (_fakeGemini) return "Das ist ein Testabschnitt aus dem Meeting.";
        string raw = GlossaryBackstops(await TranscribeMeetingRawAsync(wav, ct)); // TrayApp.Meetings.cs
        string text = Core.TextCleanup.DiscardImplausible(Core.TextCleanup.ForDictation(raw), Core.Wav.DurationSeconds(wav));
        return Core.TextCleanup.LooksLikeRefusal(text) ? "" : text;
    }

    /// <summary>End-of-meeting summary with the meeting summary model (Settings → Meetings).</summary>
    private Task<string> SummarizeMeetingAsync(string transcript, CancellationToken ct)
    {
        if (_fakeGemini) return Task.FromResult("Hauptpunkte:\n- Testmeeting ohne Gemini");
        return SummarizeMeetingWithModelAsync(Core.MeetingPrompts.Clip(transcript), ct); // TrayApp.Meetings.cs
    }

    // ---------------- glossary ----------------

    /// <summary>
    /// Copies the selection and appends it to the Glossary as is: the way to teach the spelling of a
    /// name from a document or the practice software, with no model in between (works offline too).
    /// </summary>
    private async Task AddSelectionToGlossaryAsync()
    {
        if (_state != State.Idle || _glossaryBusy || _selection != null || (_readAloud?.IsActive == true && !_readAloud.IsSpeaking))
        {
            Sounds.Error();
            return;
        }
        _glossaryBusy = true;
        try
        {
            string? selection = await _paster.CopySelectionAsync();
            var result = Core.GlossaryEdit.TryAdd(_settings.Glossary, selection, out string term);
            Core.Log.Info($"Glossary add: {result} ({term.Length} chars)");
            switch (result)
            {
                case Core.GlossaryAddResult.Added:
                    TrySave();
                    _offline.GlossaryChanged();
                    _settingsForm?.AppendGlossaryTerm(term);
                    ShowDoneIfFree($"„{term}“ steht jetzt im Glossar");
                    break;
                case Core.GlossaryAddResult.Duplicate:
                    ShowDoneIfFree($"„{term}“ stand schon im Glossar", neutral: true);
                    break;
                case Core.GlossaryAddResult.Empty:
                    Sounds.Error();
                    Notify("Nichts markiert", $"Erst ein Wort oder einen Namen markieren, dann {Hotkey.Display(_settings.GlossaryHotkey)} drücken.", ToolTipIcon.Info);
                    break;
                default:
                    Sounds.Error();
                    Notify("Zu viel markiert", $"Bitte nur ein Wort oder einen Namen markieren (eine Zeile, höchstens {Core.GlossaryEdit.MaxTermLength} Zeichen).", ToolTipIcon.Info);
                    break;
            }
        }
        catch (SelectionUnreadableException)
        {
            Sounds.Error();
            Notify("Nichts geändert", "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            Core.Log.Error("Glossary add failed", e);
            Sounds.Error();
        }
        finally
        {
            _glossaryBusy = false;
        }
    }

    // ---------------- recent texts ----------------

    private void UpdateRecentMenu()
    {
        _copyLast.Enabled = _recent.Latest != null;
        _recentMenu.DropDownItems.Clear();
        _recentMenu.Enabled = _recent.Items.Count > 0;
        foreach (var text in _recent.Items)
        {
            var item = new ToolStripMenuItem(Core.RecentTexts.Preview(text).Replace("&", "&&"), null, (_, _) => CopyRecent(text));
            _recentMenu.DropDownItems.Add(item);
        }
        if (_recent.Items.Count == 0) return;
        _recentMenu.DropDownItems.Add(new ToolStripSeparator());
        _recentMenu.DropDownItems.Add(new ToolStripMenuItem("Liste leeren", null, (_, _) => _recent.Clear()));
        _recentMenu.DropDownItems.Add(new ToolStripMenuItem("Die Liste steht nur im Arbeitsspeicher und ist nach dem Beenden weg.") { Enabled = false });
    }

    private void CopyRecent(string? text)
    {
        if (text == null) return;
        if (_paster.CopyToClipboard(text)) ShowDoneIfFree("In der Zwischenablage, mit Strg+V einfügen");
        else Notify("Kopieren fehlgeschlagen", "Die Zwischenablage war blockiert. Bitte noch einmal versuchen.", ToolTipIcon.Warning);
    }

    /// <summary>
    /// A short confirmation must not replace the recording or processing card: then a balloon says it.
    /// </summary>
    private void ShowDoneIfFree(string title, bool neutral = false)
    {
        if (_state == State.Idle) _overlay.ShowDone(title, neutral);
        else Notify("WhisperShortcut", title, ToolTipIcon.Info);
    }

    /// <summary>The local models the mode's engines need are on disk (true when it uses none).</summary>
    private bool OfflineModelsReady(Mode mode) =>
        (_settings.EffectiveSpeechEngine != Core.Engine.Local || OfflineEngine.SpeechInstalled)
        && (mode == Mode.Dictate || _settings.EffectiveTextEngine != Core.Engine.Local || OfflineEngine.LlmInstalled(_offline.SelectedLlm));

    // ---------------- UI ----------------

    /// <summary>
    /// Esc and Ctrl+1–9 belong to the other programs except while we need them: Esc while recording or processing,
    /// Ctrl+1–9 only while a Dictate Prompt recording runs.
    /// </summary>
    private void SyncSessionHotkeys()
    {
        if (_state == State.Idle) _hotkeys.Unregister(Escape);
        else if (!_hotkeys.IsRegistered(Escape)) _hotkeys.Register(Escape, OnEscape);
        bool quick = _state == State.Recording && _mode == Mode.Prompt;
        for (int i = 0; i < QuickKeys.Length; i++)
        {
            if (!quick || i >= _settings.QuickActions.Count) _hotkeys.Unregister(QuickKeys[i]);
            else if (!_hotkeys.IsRegistered(QuickKeys[i]))
            {
                int index = i;
                // Taken by another program: the card still works with a click.
                if (!_hotkeys.Register(QuickKeys[i], () => OnQuickAction(index))) Core.Log.Info($"Ctrl+{i + 1} is taken");
            }
        }
    }

    private void SetState(State state)
    {
        _state = state;
        SyncSessionHotkeys();
        SyncGlobalHotkeys();
        string key = Hotkey.Display(_stopKey);
        (string status, Icon icon) = state switch
        {
            State.Recording => (Core.UserCopy.RecordingStatus(key, prompt: _mode != Mode.Dictate), TrayIcons.Recording),
            State.Processing => (TrayProcessingStatus(), TrayIcons.Processing),
            _ when _readAloud?.IsActive == true => (_readAloud.StatusText, _readAloud.IsSpeaking ? TrayIcons.Ready : TrayIcons.Processing),
            _ when MissingKey => ("Kein API-Schlüssel eingetragen", TrayIcons.Warning),
            _ when !OfflineModelsReady(Mode.Dictate) => ("Spracherkennung noch nicht geladen", TrayIcons.Warning),
            _ when _settings.OfflineMode => ("Bereit (offline, nichts verlässt diesen PC)", TrayIcons.Ready),
            _ => (Core.UserCopy.ReadyStatus, TrayIcons.Ready),
        };
        _icon.Icon = icon;
        SetStatusText(status);
        if (state == State.Idle && _updateNoticePending)
        {
            _updateNoticePending = false;
            _updateNoticeTimer.Start();
        }
    }

    // ---------------- updates ----------------

    private void OnUpdateFound()
    {
        var update = _updates.Available;
        if (update == null) return;
        _updateItem.Text = $"Update auf Version {update.Version} installieren …";
        _updateItem.Visible = true;
        // Found from the settings window: it shows the news itself.
        if (_settingsForm != null) return;
        if (_state == State.Idle) ShowUpdateNotice();
        else _updateNoticePending = true;
    }

    /// <summary>Tray „Nach Updates suchen …“ (Mac #346 „Check for Updates…“): a found version opens the update window right away.</summary>
    private async Task CheckForUpdatesFromTrayAsync()
    {
        if (_updates.Available != null)
        {
            _updates.ShowDialog();
            return;
        }
        _checkUpdatesItem.Enabled = false;
        try
        {
            var latest = await _updates.CheckAsync(manual: true);
            if (latest != null) _updates.ShowDialog();
            else Notify("Kein Update", $"Version {UpdateService.CurrentVersion} ist die neueste.", ToolTipIcon.Info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Core.Log.Error($"Update check failed: {e.GetType().Name}");
            Notify("Keine Verbindung zu GitHub", "Später noch einmal versuchen.", ToolTipIcon.Warning);
        }
        finally { _checkUpdatesItem.Enabled = true; }
    }

    private void ShowUpdateNotice()
    {
        var update = _updates.Available;
        if (update == null) return;
        Notify($"Version {update.Version} ist da",
            "Sieh dir an, was neu ist, und installier es. Ohne Klick bleibt alles, wie es ist.",
            ToolTipIcon.Info, _updates.ShowDialog, "Ansehen");
    }

    /// <summary>
    /// Idempotent, so the order of the settings window's activate / deactivate / close events cannot
    /// leave the hotkeys off: every one of them (and every state change) just re-evaluates.
    /// </summary>
    private void SyncGlobalHotkeys(bool notify = false)
    {
        var f = _settingsForm;
        // WHY the real foreground and not Form.ActiveForm: a form activated without getting the foreground
        // (focus-steal prevention) stays "active" while the user works elsewhere. Any window of ours in
        // front counts, so the settings dialogs (MessageBox) keep the hotkeys off as well.
        // The welcome tour in front is the exception: its „Ausprobieren“ step needs the dictate shortcut.
        bool tourInFront = _welcomeForm is { IsDisposed: false, Visible: true } tour && ClipboardPaster.ForegroundRoot() == tour.Handle;
        // The chat is ours too, but it needs the shortcuts (dictating into it): with the settings open behind it,
        // the chat in front turned them all off.
        bool chatInFront = _chatWindow is { IsDisposed: false, Visible: true } chat && ClipboardPaster.ForegroundRoot() == chat.Handle;
        bool settingsInFront = f is { IsDisposed: false, Visible: true } && ClipboardPaster.ForegroundIsOwnWindow() && !tourInFront && !chatInFront;
        bool wanted = Core.HotkeyPolicy.WantGlobalHotkeys(settingsInFront, _state == State.Idle);
        // Keys saved during a recording (Saved left them pending) are taken over once it is idle again.
        bool pendingWhileOn = wanted && _globalHotkeysOn && _hotkeyCheckPending && _state == State.Idle;
        if (wanted == _globalHotkeysOn && !pendingWhileOn) return;
        if (wanted)
        {
            RegisterHotkeys(notify || _hotkeyCheckPending);
            _hotkeyCheckPending = false;
        }
        else
        {
            _hotkeys.UnregisterAll();
            _globalHotkeysOn = false;
            Core.Log.Info("Hotkeys off: settings window in front");
        }
    }

    private void SetBusy(string status)
    {
        _busyStatus = status;
        if (_state == State.Processing)
        {
            SetStatusText(TrayProcessingStatus());
            _overlay.ShowProcessing(status);
        }
    }

    /// <summary>Mac AppState processing lines: the tray says what runs, the pill shows the step (<see cref="_busyStatus"/>).</summary>
    private string TrayProcessingStatus() => _mode == Mode.Dictate && _quickAction == null
        ? Core.UserCopy.ProcessingDictationStatus
        : Core.UserCopy.ProcessingPromptStatus;

    private void SetStatusText(string status)
    {
        string tip = $"WhisperShortcut – {status}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127]; // NotifyIcon throws above 127 characters; also the tray's screen-reader name
        _statusItem.Text = status;
    }

    /// <summary>Popup like the Mac's; <paramref name="onAction"/> becomes a button labelled <paramref name="actionLabel"/>.</summary>
    private void Notify(string title, string text, ToolTipIcon icon, Action? onAction = null, string? actionLabel = null)
    {
        Core.Log.Info($"Notify: {title}");  // title only: the text can quote an error message
        _toast.Present(title, text, icon, onAction, actionLabel);
    }

    /// <summary>
    /// The first time the settings window closes: say that the app keeps running and where the icon is.
    /// Windows 11 hides new tray icons behind the ^ arrow, and a closed window looked like a closed app.
    /// </summary>
    private void ShowTrayHintOnce()
    {
        var marker = Path.Combine(Core.AppData.SettingsFolder, "tray-hint-shown");
        if (File.Exists(marker)) return;
        Notify("WhisperShortcut läuft weiter",
            // Windows shows four lines of a balloon; longer text is cut off.
            "Die Kürzel gehen auch bei geschlossenem Fenster. Das Symbol ist unten rechts, "
            + "oft hinter dem Pfeil ^. Beenden: Rechtsklick auf das Symbol.",
            ToolTipIcon.Info);
        try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); }
        catch (Exception e) { Core.Log.Error("Writing tray hint marker failed", e); }
    }

    /// <summary><c>--settings</c>: open the settings window right away (first setup, VM screenshots).</summary>
    public void OpenSettingsOnStart() => _ui.Post(_ => ShowSettings(), null);

    /// <summary>
    /// The welcome tour (Mac WelcomeWindowController): at start until it is finished, from the tray menu
    /// („Einführung …“; Mac #346 took it off Über) and <c>--welcome</c>. One window; a second call brings it to the front.
    /// </summary>
    public void OpenWelcome() => _ui.Post(_ => ShowWelcome(), null);

    private void ShowWelcome()
    {
        if (_welcomeForm is { IsDisposed: false } open)
        {
            open.Reveal();
            return;
        }
        // WHY one at a time: both windows write keys and engines; the settings window would later store its stale fields
        // over what the tour just saved. Closing applies its pending edits first.
        _settingsForm?.Close();
        var form = _welcomeForm = new WelcomeForm(_settings, _offline, _gemini, _openAi);
        form.Saved += AfterTourSaved;
        form.Completed += () =>
        {
            AfterTourSaved();
            // Mac: finished, but nothing can transcribe yet → the settings open on „Konten“.
            if (!Core.WelcomeTour.TranscriptionReady(_settings, OfflineEngine.SpeechInstalled))
                _ui.Post(_ => ShowSettingsPage(SettingsForm.AccountsPage), null);
        };
        // Moving between the tour and the settings window turns the global shortcuts on or off (see SyncGlobalHotkeys).
        form.Activated += (_, _) => _ui.Post(_ => SyncGlobalHotkeys(), null);
        form.FormClosed += (_, _) =>
        {
            if (_welcomeForm == form) _welcomeForm = null;
            if (_settingsAfterTour)
            {
                _settingsAfterTour = false;
                _ui.Post(_ => ShowSettings(), null);
            }
            ShowTrayHintOnce();
        };
        form.Show();
        form.Reveal();
    }

    // A key, the offline choice or offline mode changed in the tour: the same follow-ups as a settings save.
    private void AfterTourSaved()
    {
        if (_settings.OfflineMode) _promptMemory.Clear();
        SetState(_state); // tooltip "Bereit" instead of "Kein API-Schlüssel"
        _offline.Prewarm();
        _mcp.SyncOfflineMode();
        AfterOfflineMaybeChanged();
    }

    // Offline mode may just have been switched on: cloud read-aloud and a streaming cloud chat answer must stop.
    private void AfterOfflineMaybeChanged()
    {
        _readAloud.StopIfCloudWhileOffline();
        _chatWindow?.CancelIfCloudWhileOffline();
    }

    /// <summary>Opens the settings window on <paramref name="page"/> (e.g. „Konten“ from a missing-key popup).</summary>
    private void ShowSettingsPage(string page)
    {
        ShowSettings();
        _settingsForm?.ShowPage(page);
    }

    /// <summary>
    /// The open-settings hotkey (Mac SettingsManager.toggleSettings): closes the window when it is open, opens it otherwise.
    /// While the window is in front the global hotkeys are off; SettingsForm closes itself on the same key then.
    /// </summary>
    private void ToggleSettings()
    {
        if (_settingsForm is { IsDisposed: false, Visible: true } form)
        {
            Core.Log.Info("Hotkey Settings: closing the window");
            form.Close();
            return;
        }
        ShowSettings();
    }

    // ---------------- settings window built in advance ----------------

    private SettingsForm? _spareSettings;
    private string? _spareFingerprint;
    private System.Windows.Forms.Timer? _spareTimer;

    /// <summary>
    /// The build blocks the UI thread for one to two seconds on a small PC. Started while the user works, it delayed the
    /// next hotkey by that much (typical: close the settings, then dictate to try the change). It waits until neither
    /// keyboard nor mouse were used for this long.
    /// </summary>
    private const long SpareBuildIdleMs = 4000;

    /// <summary>
    /// A few seconds from now, when nothing else runs, build the next settings window, and once the chat window
    /// (Mac: both open instantly). One window per tick: each build holds the UI thread for a moment.
    /// </summary>
    private void ScheduleSpareSettings(int delayMs = 3000)
    {
        _spareTimer?.Dispose();
        _spareTimer = new System.Windows.Forms.Timer { Interval = delayMs };
        _spareTimer.Tick += (_, _) =>
        {
            // Busy (recording, a window open): try again later; the build blocks the UI thread for a moment.
            if (_state != State.Idle || _settingsForm != null || _welcomeForm is { IsDisposed: false }) return;
            if (NativeMethods.IdleMilliseconds() < SpareBuildIdleMs) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The builds below pump messages; a hotkey must not start a recording inside them (it would capture our own window).
            _hotkeys.HoldActions();
            try { SpareBuildTick(watch); }
            finally { _hotkeys.ReleaseActions(); }
        };
        _spareTimer.Start();
    }

    private void SpareBuildTick(System.Diagnostics.Stopwatch watch)
    {
        if (_spareSettings is not { IsDisposed: false } || _spareFingerprint != _settings.Fingerprint())
        {
            _spareSettings?.Dispose();
            _spareSettings = new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, _mcp);
            _spareSettings.Prewarm();
            _spareFingerprint = _settings.Fingerprint();
            Core.Log.Info($"Settings window built in advance in {watch.ElapsedMilliseconds} ms");
            return; // the chat, if still needed, on the next tick
        }
        _spareTimer!.Dispose();
        _spareTimer = null;
        if (_chatWindow is { IsDisposed: false }) return;
        // The chat lives until the app quits (TrayApp.Chat.cs): built once, its first Alt+6 only makes it visible.
        ChatWindow().Prewarm();
        Core.Log.Info($"Chat window built in advance in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The window built in advance, if the settings are still what it shows; otherwise null (and it goes).</summary>
    private SettingsForm? TakeSpareSettings()
    {
        var spare = _spareSettings;
        _spareSettings = null;
        if (spare is not { IsDisposed: false }) return null;
        if (_spareFingerprint == _settings.Fingerprint()) return spare;
        Core.Log.Info("Settings changed since the window was built in advance: building a new one");
        spare.Dispose();
        return null;
    }

    private void ShowSettings()
    {
        if (_welcomeForm is { IsDisposed: false } tour)
        {
            // See ShowWelcome: the tour and the settings window are never open together.
            tour.Reveal();
            _settingsAfterTour = true;
            Notify("Einführung läuft", "Die Einstellungen öffnen sich, sobald du die Einführung schließt oder abschließt.", ToolTipIcon.Info);
            return;
        }
        if (_settingsForm != null)
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            _settingsForm.BringToFront();
            return;
        }
        if (_state != State.Idle)
        {
            Notify("Aufnahme läuft", "Die Einstellungen gehen erst danach auf. Bitte die Aufnahme beenden, "
                + "warten, bis der Text da ist, und dann noch einmal klicken.", ToolTipIcon.Info);
            return;
        }

        // While the window is in front the global hotkeys are off, so the hotkey field can record
        // Ctrl+Shift+D itself. Behind Word or minimized, they work as usual.
        var form = _settingsForm = TakeSpareSettings() ?? new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, _mcp);
        // Activation messages can still arrive after FormClosed: only the open window may re-evaluate.
        _settingsForm.Activated += (_, _) => { if (_settingsForm == form) SyncGlobalHotkeys(); };
        // Posted: while Deactivate runs the new foreground window is not set yet.
        _settingsForm.Deactivate += (_, _) => { if (_settingsForm == form) _ui.Post(_ => SyncGlobalHotkeys(), null); };
        // Edits apply by themselves (shortly after each one): apply right away, check the hotkeys once the window leaves the front.
        var glossary = string.Join("\n", _settings.Glossary);
        _settingsForm.Saved += () =>
        {
            // Only a changed glossary reloads the offline recognizer (a few seconds); most applies are other edits.
            var nowGlossary = string.Join("\n", _settings.Glossary);
            if (nowGlossary != glossary) { glossary = nowGlossary; _offline.GlossaryChanged(); }
            Sounds.Enabled = _settings.Sounds;
            _hotkeyCheckPending = true;
            // The window may already be out of front (the save runs 500 ms after the last edit, on the UI thread): then the
            // hotkeys were registered from the old values and nothing else would re-arm them. Registered = not in front.
            // Only while idle: during a recording the key that started it must keep stopping it (the check runs later).
            if (_globalHotkeysOn && _state == State.Idle)
            {
                RegisterHotkeys(notify: true);
                _hotkeyCheckPending = false;
            }
            if (_settings.OfflineMode) _promptMemory.Clear();
            SetState(_state); // key may have been added: tooltip "Bereit" instead of "Kein API-Schlüssel"
            _offline.Prewarm(); // offline mode may just have been switched on
            DropScreenshotIfNotAllowed();
            _mcp.SyncOfflineMode();
            AfterOfflineMaybeChanged();
        };
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            ScheduleSpareSettings();
            ShowTrayHintOnce();
            // Posted so it runs after any activation message still queued for the closing form; forced
            // because the hotkeys may have been changed in the window.
            _ui.Post(_ =>
            {
                _globalHotkeysOn = false;
                SyncGlobalHotkeys(notify: true);
            }, null);
        };
        _settingsForm.Show();
        _settingsForm.Activate();
        _settingsForm.BringToFront();
    }

    /// <summary>
    /// An AI app changed the settings (directly, through an accepted suggestion, or an Undo): apply them like
    /// „Speichern“ does. An open settings window still shows the old values, so it reopens on the same page;
    /// writes wait while it has unsaved edits (<see cref="McpService.WriteBlockedReason"/>), so nothing is lost.
    /// </summary>
    private void AfterMcpApply()
    {
        Sounds.Enabled = _settings.Sounds;
        if (_globalHotkeysOn) RegisterHotkeys(); // a hotkey may have changed; otherwise they register when the window leaves the front
        SetState(_state);
        _offline.Prewarm(); // the engine may have been switched to this PC
        _offline.GlossaryChanged();
        DropScreenshotIfNotAllowed(); // a local text engine must not get a held screenshot later
        if (_settingsForm is not { IsDisposed: false } form) return;
        _ui.Post(_ =>
        {
            if (_settingsForm != form || form.HasUnsavedChanges) return;
            var (page, location) = (form.CurrentPage, form.Location);
            form.CloseDiscarding();
            ShowSettings();
            if (_settingsForm is { } fresh)
            {
                fresh.Location = location;
                fresh.ShowPage(page);
            }
        }, null);
    }

    private void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception e)
        {
            Core.Log.Error("Saving settings failed", e);
            Notify("Speichern fehlgeschlagen",
                e is Core.DiskFullException ? e.Message : "Die Einstellungen konnten nicht gespeichert werden.",
                ToolTipIcon.Warning);
        }
    }

    private static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Core.Log.DefaultPath) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error("Opening log failed", e); }
    }

    private void Quit()
    {
        _meeting.Shutdown(); // ends a running meeting properly; audio not transcribed yet stays on disk
        _cts?.Cancel();
        _selectionCts?.Cancel();
        (_audio as MicAudioSource)?.Cancel(discardCheckpoint: false);
        _icon.Visible = false;
        _icon.Dispose();
        _keyUpTimer.Dispose();
        _liveRetryTimer.Dispose();
        _lastLive.Dispose();
        _templateReplay.Clear();
        _replayExpiry.Dispose();
        _snipCts?.Cancel();
        _shotExpiry.Dispose();
        _updateNoticeTimer.Dispose();
        _updates.Dispose();
        _readAloud.Dispose();
        DisposeChat();
        _spareTimer?.Dispose();
        _spareSettings?.Dispose(); // the window built in advance: its timers and the AI-app subscription go with it
        _overlay.Dispose();
        _toast.Dispose();
        _meeting.Dispose();
        _hotkeys.Dispose();
        _offline.Dispose();
        _mcp.Dispose();
        ExitThread();
    }
}
