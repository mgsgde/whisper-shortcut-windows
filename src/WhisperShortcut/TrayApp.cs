using System.Diagnostics;

namespace WhisperShortcut;

internal enum Mode { Dictate, Prompt, Template }

/// <summary>
/// Tray icon, hotkeys and the recording state machine. Same toggle behaviour as the Mac:
/// 1st press starts, 2nd press stops and processes, a press while processing cancels.
/// Everything runs on the UI thread; no window ever takes focus during a recording.
/// </summary>
internal sealed partial class TrayApp : ApplicationContext
{
    private readonly string? _testWav;
    private readonly bool _fakeGemini;
    private readonly NotifyIcon _icon;
    private readonly HotkeyService _hotkeys;
    private readonly ClipboardPaster _paster;
    private readonly Core.Settings _settings;
    private readonly Core.GeminiClient _gemini;
    private readonly Core.OpenAiClient _openAi;
    private readonly OfflineEngine _offline;
    private readonly EngineSet _engines;
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

    // The run in progress, from the hotkey press to the end of its processing; null = idle. Set up in StartRecording
    // and RunAgain, torn down by Take.Dispose.
    private Take? _take;
    private FailedRun? _lastFailed;
    private bool IsIdle => _take == null;
    private bool IsRecording => _take is { Phase: TakePhase.Recording };
    private bool IsProcessing => _take is { Phase: TakePhase.Processing };
    private string PhaseName => _take?.Phase.ToString() ?? "Idle";
    /// <summary>The take that is still recording: a late event of an earlier take must not stop the next one.</summary>
    private bool IsRunning(Take take) => ReferenceEquals(_take, take) && take.Phase == TakePhase.Recording;

    private long _recordingSerial; // the last Take.Serial handed out
    private long _rightCtrlRecording = -1;
    /// <summary>What the previous live dictation ended with, while nothing has moved the caret since.</summary>
    private readonly LiveInsertionMemory _lastLive;
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
        _appliedSettings = Core.SettingsDelta.Snapshot.Of(_settings);
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
        _engines = new EngineSet(_settings, new LocalEngines(_offline), _gemini, _openAi);
        _offline.Prewarm();
        PrepareSpeechGate();

        _hotkeys = new HotkeyService();
        _rightCtrl = new Core.RightCtrlDictationToggle(new RightCtrlBridge(this), () => Environment.TickCount64);
        _lastLive = new LiveInsertionMemory(IsOwnShortcut);
        _paster = new ClipboardPaster(_hotkeys.Handle);
        _paster.IsOwnTextWindow = h =>
            (_settingsForm is { IsDisposed: false, IsHandleCreated: true } s && s.Handle == h)
            || (_chatWindow is { IsDisposed: false, IsHandleCreated: true } c && c.Handle == h);
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
        _updates = new UpdateService(_settings, () => IsIdle && _meeting?.IsActive != true && _readAloud?.IsActive != true, Quit);
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
            (title, text, icon, open) => Notify(title, text, icon, open, open == null ? null : Core.UserCopy.ActionOpenFolder), TrySave,
            () => Core.HotkeySlots.Assignments(_settings).Where(a => a.Action != Core.HotkeyAction.Meeting).Select(a => (a.Owner, a.Key)).ToList(),
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
        _mcp = new McpService(_settings, _ui, () => !IsIdle, AfterMcpApply);
        _mcp.StartIfEnabled();
        ScheduleSpareSettings(5000);
        _readAloud = new ReadAloudController(_settings, _paster, (t, m, i, a) => Notify(t, m, i, a), () => ShowSettings(), TrySave, _ui);
        _readAloud.StateChanged += () =>
        {
            if (IsIdle) RefreshStatus();
            UpdateMenuRows();
        };

        _keyUpTimer.Tick += (_, _) => PollArmedKey();
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
        RefreshStatus();
        // A finished download changes the idle status ("Modelle noch nicht geladen" → "Bereit").
        _offline.DownloadChanged += () => { if (IsIdle) RefreshStatus(); };
        _overlay.QuickActionClicked += OnQuickAction;
        _overlay.CancelClicked += OnEscape; // the pill's ✕ (Mac): discard the recording or cancel the processing
        _updates.Found += OnUpdateFound;
        _updateNoticeTimer.Tick += (_, _) =>
        {
            _updateNoticeTimer.Stop();
            if (IsIdle) ShowUpdateNotice();
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
        var (kind, templateId) = Core.RecordingKind.Parse(rec.Mode);
        // A Template take replays with its template (found by id); a vanished template or an old checkpoint without id
        // falls back to a plain dictation. Live replays explicitly as a normal dictation of the recorded audio.
        Core.DictationTemplate? template = kind == Core.RecordingKindName.Template && templateId != null
            ? _settings.Templates.FirstOrDefault(t => t.Id == templateId && t.IsUsable)?.Clone()
            : null;
        bool lostTemplate = kind == Core.RecordingKindName.Template && template == null;
        var replayKind = template != null ? Core.RecordingKindName.Template
            : kind == Core.RecordingKindName.Prompt ? Core.RecordingKindName.Prompt : Core.RecordingKindName.Dictate;
        _lastFailed = new FailedRun(rec.Wav, replayKind, rec.Seconds, Template: template,
            StopKey: replayKind == Core.RecordingKindName.Prompt ? _settings.PromptHotkey
                : template != null ? (template.Hotkey.Length > 0 ? template.Hotkey : _settings.TemplateHotkey)
                : _settings.DictateHotkey);
        Notify("Aufnahme unterbrochen",
            $"Eine {rec.Seconds:0} s lange Aufnahme war noch nicht fertig. Der Text landet nach dem Transkribieren{(lostTemplate ? " ohne Vorlage" : "")} in der Zwischenablage.",
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
        // Assignments holds only the shortcuts that are on and assigned (Mac isEnabled); those are registered.
        var assignments = Core.HotkeySlots.Assignments(_settings).ToList();
        foreach (var a in assignments.Where(a => a.Action is not (Core.HotkeyAction.TemplatePicker or Core.HotkeyAction.Template) && !a.IsRightCtrlSwitch))
        {
            // RightCtrl alone needs the keyboard hook, which RegisterHotKey is not: report it instead of registering
            // a bare VK_RCONTROL.
            if (!Hotkey.TryParse(a.Key, out var key) || key.IsRightCtrl || !_hotkeys.Register(key, ActionFor(a.Action)))
                failed.Add(Hotkey.Display(a.Key));
        }
        // Right Ctrl alone = the Mac's Fn key (on by default, Mac fnKeyDictation).
        if (_settings.RightCtrlDictation && !_hotkeys.RegisterRightCtrl(_rightCtrl))
            failed.Add(Core.HotkeyText.RightCtrlDisplay);

        // Template keys come after the app's own, which win on a clash (see TemplateHotkeys).
        var appHotkeys = assignments
            .Where(a => a.Action is not (Core.HotkeyAction.TemplatePicker or Core.HotkeyAction.Template) && !a.IsRightCtrlSwitch)
            .Select(a => (a.Owner, a.Key)).ToArray();
        var templates = _settings.Templates;
        var pickerActive = Core.TemplateHotkeys.PickerIsActive(_settings.TemplateHotkey, templates, appHotkeys, Core.HotkeySlots.Normalize);
        if (pickerActive)
        {
            var stored = _settings.TemplateHotkey;
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, () => OnTemplateHotkey(null, stored)))
                failed.Add(Hotkey.Display(stored));
        }
        var bindings = Core.TemplateHotkeys.ActiveBindings(templates, appHotkeys, _settings.TemplateHotkey, Core.HotkeySlots.Normalize);
        foreach (var (template, stored) in bindings)
        {
            var id = template.Id;
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, () => OnTemplateHotkey(id, stored)))
                failed.Add(Hotkey.Display(stored));
        }
        // Counts and positions only: template names are the user's own words.
        var skipped = templates.Select((t, i) => (t, i))
            .Where(x => x.t.IsUsable && Core.HotkeySlots.Normalize(x.t.Hotkey) != null && !bindings.Any(b => b.Template.Id == x.t.Id))
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

    /// <summary>What a registered shortcut does.</summary>
    private Action ActionFor(Core.HotkeyAction action) => action switch
    {
        Core.HotkeyAction.Dictate => () => OnHotkey(Mode.Dictate),
        Core.HotkeyAction.Prompt => () => OnHotkey(Mode.Prompt),
        Core.HotkeyAction.Glossary => () => _ = AddSelectionToGlossaryAsync(),
        Core.HotkeyAction.Meeting => _meeting.Toggle, // no meeting key by default: the tray menu starts a meeting
        Core.HotkeyAction.LiveDictate => OnLiveHotkey,
        Core.HotkeyAction.Screenshot => OnScreenshotHotkey,
        Core.HotkeyAction.ReadAloud => OnReadAloudHotkey,
        Core.HotkeyAction.Chat => ToggleChat, // TrayApp.Chat.cs
        Core.HotkeyAction.OpenSettings => ToggleSettings,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// The menu rows as the Mac shows them (makeItem, updateUI): every action row is clickable and shows its shortcut
    /// when it is on; Diktieren and Dictate Prompt read „… beenden“ while their own recording runs; „Stopp“ and the
    /// recovery rows only appear when they can do something.
    /// </summary>
    private void UpdateMenuRows()
    {
        static string Key(string active) => active.Length > 0 ? Hotkey.Display(active) : "";
        bool idle = IsIdle;
        _stopItem.Visible = _stopSeparator.Visible = !idle || _readAloud?.IsActive == true;
        _proposalsItem.Visible = _mcp != null && _mcp.Proposals.Pending.Count > 0;
        _finishSetupItem.Visible = !_settings.WelcomeCompleted;
        _welcomeItem.Visible = _settings.WelcomeCompleted; // Mac: „Einführung …“ hides while „Einrichtung abschließen …“ shows
        var recording = _take is { Phase: TakePhase.Recording } r ? r.Kind : (Core.RecordingKindName?)null;
        _dictateItem.Text = recording == Core.RecordingKindName.Dictate ? "Diktat beenden" : "Diktieren";
        _promptItem.Text = recording == Core.RecordingKindName.Prompt ? "Prompt-Diktat beenden" : "Dictate Prompt";
        _readAloudItem.Text = _readAloud?.IsActive == true ? "Vorlesen beenden" : "Vorlesen";
        foreach (var (action, item) in new[]
        {
            (Core.HotkeyAction.Dictate, _dictateItem), (Core.HotkeyAction.Prompt, _promptItem), (Core.HotkeyAction.Screenshot, _screenshotItem),
            (Core.HotkeyAction.ReadAloud, _readAloudItem), (Core.HotkeyAction.Glossary, _glossaryItem), (Core.HotkeyAction.LiveDictate, _liveItem),
            (Core.HotkeyAction.Chat, _chatItem), (Core.HotkeyAction.OpenSettings, _settingsItem),
        })
            if (item != null) item.ShortcutKeyDisplayString = Key(Core.HotkeySlots.Of(action).Active(_settings));
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
        Core.Log.Info($"Menu Stopp in state {PhaseName}");
        if (IsRecording) _ = StopAndProcessAsync();
        else if (IsProcessing) _take?.Cts?.Cancel();
        else if (_readAloud.IsActive) _readAloud.Stop();
    }

    private void OnHotkey(Mode mode)
    {
        var stored = mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey;
        // Stop, cancel and busy stay as they are: the Diktieren key ends whichever recording runs, Live included (Mac).
        HandleHotkeyPress(mode, stored, canStopOrCancel: true,
            () => { if (mode == Mode.Dictate) StartDictateByMode(stored, "Diktieren shortcut"); else StartRecording(Take.KindOf(mode), stored); });
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
                StartRecording(Core.RecordingKindName.Dictate, stored);
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
        Core.HotkeySlots.Assignments(_settings).Where(a => !a.IsRightCtrlSwitch)
            .Any(a => Hotkey.TryParse(a.Key, out var h) && (h.Key
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
            if (!app.IsIdle) return false;
            Core.Log.Info("Hotkey RightCtrl in state Idle");
            app.StartDictateByMode(Core.HotkeyText.RightCtrl, "Right Ctrl");
            // Right Ctrl ends only the recording it started, never one another shortcut began, even of the same kind.
            app._rightCtrlRecording = app._take is { Phase: TakePhase.Recording } t ? t.Serial : -1;
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

        public bool IsRecording() => app._take is { Phase: TakePhase.Recording, Mode: Mode.Dictate } t
            && t.Serial == app._rightCtrlRecording;

        public bool IsProcessing() => app._take is { Phase: TakePhase.Processing, Mode: Mode.Dictate };

        public void CancelProcessing() => app._take?.Cts?.Cancel();
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
        HandleHotkeyPress(Mode.Template, stored, canStopOrCancel: _take?.Mode == Mode.Template, () =>
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
            StartRecording(Core.RecordingKindName.Template, stored, found.Clone());
            if (IsRecording)
            {
                _settings.LastTemplateId = found.Id;
                TrySave();
            }
        });

    /// <summary>
    /// The toggle shared by every hotkey: idle starts, a press while recording stops, while processing cancels.
    /// Presses the gate rejects, or that may not stop the current run, are only logged.
    /// </summary>
    private void HandleHotkeyPress(Mode mode, string stored, bool canStopOrCancel, Action startIdle)
    {
        var label = mode.ToString();
        Core.Log.Info($"Hotkey {label} in state {PhaseName}");
        // Vorlesen copies the selection while preparing: a second copy now could lose the user's clipboard.
        if (IsIdle && mode != Mode.Dictate && _readAloud?.IsActive == true && !_readAloud.IsSpeaking)
        {
            Core.Log.Info($"Hotkey {label} ignored: Vorlesen is preparing");
            Sounds.Error();
            return;
        }
        var now = Environment.TickCount64;
        var gateOpen = IsIdle || _pressGate.Accept(now);
        if (!IsIdle && !gateOpen)
        {
            Core.Log.Info($"Hotkey {label} in state {PhaseName} ignored: {(_pressGate.Released ? "within 300 ms" : "key still held")}");
            return;
        }
        if (!IsIdle && !canStopOrCancel)
        {
            Core.Log.Info($"Hotkey {label} in state {PhaseName} ignored: a {_take!.Mode} run is active");
            return;
        }
        var decision = Core.RecordingHotkeyPolicy.Decide(
            recording: IsRecording,
            processing: IsProcessing,
            sameMode: mode == _take?.Mode,
            gateOpen: gateOpen);
        if (decision == Core.RecordingHotkeyDecision.Ignore)
        {
            Core.Log.Info($"Hotkey {label} in state {PhaseName} ignored: other mode is already running");
            return;
        }
        var before = PhaseName;
        switch (decision)
        {
            case Core.RecordingHotkeyDecision.Start:
                startIdle();
                break;
            case Core.RecordingHotkeyDecision.Stop:
                _ = StopAndProcessAsync();
                break;
            case Core.RecordingHotkeyDecision.Cancel:
                _take?.Cts?.Cancel();
                break;
        }
        if (PhaseName != before && Hotkey.TryParse(stored, out var key))
            ArmPressGate(key.Key, now);
    }

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
        Core.Log.Info($"Esc in state {PhaseName}");
        // Esc in the screenshot picker cancels the picker, not the recording behind it.
        if (_snipCts != null) { _snipCts.Cancel(); return; }
        if (_take is not { } take) return;
        if (take.Phase == TakePhase.Processing)
        {
            take.Cts?.Cancel(); // same path as a hotkey press during processing
            return;
        }
        // Text already inserted stays: it is in another window.
        if (take.Live != null) _paster.EndLive(LiveClipboardAtEnd(null, false), null);
        EndTake(take); // stops the microphone and the selection copy; Esc also drops the crash checkpoint
        Core.RecordingCheckpoint.DiscardAll();
        Sounds.Stop();
        _overlay.HideOverlay();
        RefreshStatus();
    }

    /// <summary>Tears the run down (<see cref="Take.Dispose"/>) and leaves idle; the caller then refreshes overlay and status.</summary>
    private void EndTake(Take take)
    {
        take.Dispose();
        if (ReferenceEquals(_take, take)) _take = null;
    }

    // Live diktieren ends: the clipboard setting decides between restore, keep and clear (Mac "After auto-paste").
    private Core.LiveClipboardEnd LiveClipboardAtEnd(Core.LiveDictationOutcome? outcome, bool fallbackHasText) =>
        Core.LiveDictation.ClipboardAtEnd(_settings.RestoreClipboard,
            clearAfterPaste: _settings.ClipboardAfterPaste == Core.ClipboardAfterPaste.Clear, outcome, fallbackHasText);

    /// <summary>
    /// A quick instruction was picked while recording (Ctrl+N or a click on the card): stop and apply
    /// it to the selection, or with nothing marked to what was just said.
    /// </summary>
    private void OnQuickAction(int index)
    {
        if (_take is not { Phase: TakePhase.Recording, Kind: Core.RecordingKindName.Prompt } take || index >= _settings.QuickActions.Count) return;
        Core.Log.Info($"Quick action {index + 1}");
        take.QuickAction = _settings.QuickActions[index];
        _ = StopAndProcessAsync();
    }

    // ---------------- read aloud ----------------

    /// <summary>
    /// Vorlesen hotkey and menu row (Mac triggerReadSelectedTextAloud): a second press stops it. Ignored while a recording
    /// or its processing runs and during a meeting, like the Mac; speaking itself does not block a new dictation.
    /// </summary>
    private void OnReadAloudHotkey()
    {
        Core.Log.Info($"Hotkey ReadAloud in state {PhaseName}, reading {_readAloud.IsActive}");
        if (_readAloud.IsActive)
        {
            _readAloud.Toggle();
            return;
        }
        if (!IsIdle || _glossaryBusy || _meeting.IsActive)
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
        Core.Log.Info($"Hotkey Screenshot in state {PhaseName}");
        if (_snipCts != null)
        {
            _snipCts.Cancel();
            return;
        }
        if (IsProcessing) return; // the request is already on its way
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
            if (_take is { Phase: TakePhase.Recording, Kind: Core.RecordingKindName.Prompt or Core.RecordingKindName.Template, Shot: null } running)
            {
                running.Shot = image;
                Core.Log.Info($"Screenshot attached to the running {running.Kind} ({image.Data.Length / 1024} KB)");
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
    private static EngineStep StepFor(Mode mode) => mode == Mode.Dictate ? EngineStep.Dictation : EngineStep.Prompt;

    /// <summary>HEAD the cloud API origin so TCP+TLS is done before the user presses Stop.</summary>
    private void PrewarmCloudConnections(Mode mode)
    {
        if (_fakeGemini) return;
        _engines.Prewarm(StepFor(mode));
    }

    /// <summary>Any mode lacks a key: for the tray tooltip.</summary>
    private bool MissingKey => _engines.Check(EngineStep.Prompt, checkKeys: !_fakeGemini).MissingKey != null;

    private void StartRecording(Core.RecordingKindName kind, string stored, Core.DictationTemplate? template = null, LiveRun? live = null)
    {
        var mode = Take.ModeOf(kind);
        // The snip UI is open: its image is about to arrive over the clipboard, a Ctrl+C now would collide with it.
        if (_snipCts != null)
        {
            Core.Log.Info($"Hotkey {mode} ignored: waiting for the snip UI");
            return;
        }
        var ready = _engines.Check(StepFor(mode), checkKeys: !_fakeGemini);
        if (ready.ModelsMissing)
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
        if (ready.LlmCpuUnsupported)
        {
            Core.Log.Info($"Hotkey {mode} with the local text engine, but this CPU cannot run the language model");
            Sounds.Error();
            Notify(Core.UserCopy.OfflineModelErrorTitle, Core.LlamaServerHost.UnsupportedCpuMessage, ToolTipIcon.Warning, ShowSettings, Core.UserCopy.ActionOpenSettings);
            return;
        }
        if (ready.MissingKey is { } provider)
        {
            Core.Log.Info($"Hotkey pressed but no {provider} key (Schlüssel fehlt)");
            Sounds.Error();
            Notify(Core.UserCopy.ProviderErrorTitle(Core.GeminiErrorKind.NoKey, provider), $"Noch kein {provider}-Schlüssel eingetragen. Du trägst ihn in den Einstellungen unter „Konten“ ein.",
                ToolTipIcon.Warning, () => ShowSettingsPage(SettingsForm.AccountsPage), Core.UserCopy.ActionOpenSettings);
            return;
        }

        // How long the press waits for its feedback (Mac: the pill is there at once); the log line below says where it went.
        var feedback = Stopwatch.StartNew();
        // Mac pastes only into the app that was in front now; the hotkey was pressed in the user's window.
        // The take is idle until the microphone runs: a failed start leaves nothing behind (Dispose).
        var take = new Take(kind, stored, _paster) { Template = template, Live = live, PasteTarget = ClipboardPaster.ForegroundRoot() };
        // Copy the selection right away, while the user's selection is still there. It runs while recording.
        if (mode != Mode.Dictate) take.StartSelectionCopy();
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
            take.Audio = new Core.FileAudioSource(_testWav);
        }
        else
        {
            mic = new MicAudioSource(limit, _settings.Microphone, checkpointMode: take.CheckpointMode);
            if (live != null) mic.PcmAvailable += live.Feed;
            mic.MaxDurationReached += () => _ui.Post(_ =>
            {
                if (!IsRunning(take)) return;
                Notify("Aufnahme beendet", $"Nach {limit.TotalMinutes:0} Minuten endet die Aufnahme automatisch. Der Text wird jetzt verarbeitet.", ToolTipIcon.Info);
                _ = StopAndProcessAsync();
            }, null);
            mic.DeviceLost += () => _ui.Post(_ =>
            {
                if (!IsRunning(take)) return;
                Notify("Mikrofon getrennt", "Aufnahme beendet, das Bisherige wird verarbeitet.", ToolTipIcon.Warning);
                _ = StopAndProcessAsync();
            }, null);
            mic.CheckpointFailed += () => _ui.Post(_ =>
            {
                Notify("Datenträger voll", Core.DiskSpace.RecordingMessage, ToolTipIcon.Warning);
            }, null);
            take.Audio = mic;
        }

        long beforeMic = feedback.ElapsedMilliseconds;
        try
        {
            take.Audio!.Start();
        }
        catch (Exception e)
        {
            Core.Log.Error("Recording start failed", e);
            take.Dispose(); // also the live session: no clipboard change yet, BeginLive was not called
            Sounds.Error();
            Notify("Aufnahme nicht möglich",
                e is MicrophoneUnavailableException ? e.Message : Core.UserCopy.TechnicalFallback("Die Aufnahme konnte nicht starten.", e.Message),
                ToolTipIcon.Error);
            return;
        }
        long micMs = feedback.ElapsedMilliseconds - beforeMic;
        Sounds.Start();
        take.Serial = ++_recordingSerial;
        _take = take;
        RefreshStatus();
        // One turn only: a Dictate run leaves the image for the next Dictate Prompt or template run.
        take.Shot = mode != Mode.Dictate && Core.ScreenshotPolicy.Allowed(_settings) ? _heldShot.Take() : null;
        if (take.Shot != null) Core.Log.Info($"{mode}: screenshot attached ({take.Shot.Data.Length / 1024} KB)");
        _overlay.ShowRecording(mode, Hotkey.Display(stored), limit,
            mic != null ? () => mic.Level : null, mic != null ? () => mic.PeakSoFar : null,
            mode == Mode.Prompt ? _settings.QuickActions : null,
            templateName: mode == Mode.Template ? template!.DisplayName : null, screenshot: take.Shot != null,
            title: live != null ? Core.UserCopy.LiveTitle : null);
        Core.Log.Info($"{mode}: overlay after {feedback.ElapsedMilliseconds} ms (setup {beforeMic} ms, microphone {micMs} ms)");
        if (kind == Core.RecordingKindName.Dictate && _welcomeForm is { TakesTryResult: true } tour && take.PasteTarget == tour.Handle)
            tour.ShowTryListening();
        if (take.Selection is { } pending)
        {
            _ = pending.ContinueWith(t =>
            {
                if (take.Selection == pending && t.IsCompletedSuccessfully) _overlay.SetSelection(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private Task StopAndProcessAsync() =>
        _take!.Kind == Core.RecordingKindName.Live ? ProcessLiveAsync(_take) : ProcessAsync(_take, null);

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
        var targetApp = FocusedTextContext.ProcessName(front);
        var readCaret = FocusedTextContext.CharacterBeforeCaret(front);
        var caret = Core.LastInsertion.Seed(readCaret, _lastLive.Last, targetApp, DateTime.UtcNow);
        Core.Log.Info(caret != readCaret
            ? $"LIVE-DICTATE: caret context from last dictation ({caret.LogKind})"
            : $"LIVE-DICTATE: caret context {caret.LogKind}");
        _lastLive.Forget(null); // the new session records its own ending; the old memory is spent either way
        // Like Diktieren: "Nur gesprochen" applies offline, outside meetings (Live never runs in a meeting).
        var punctuation = Core.PunctuationModes.Effective(_settings.PunctuationMode, isOffline: true, duringMeeting: false);
        // The session exists before the recording starts, so a failed start (Take.Dispose) finds it and discards it.
        var live = new LiveRun(_ui, punctuation, targetApp, caret, DecodeLivePieceAsync,
            (text, target) => _paster.PasteLivePieceAsync(text, target, CancellationToken.None));
        StartRecording(Core.RecordingKindName.Live, stored, live: live);
        if (!IsRecording)
        {
            live.Dispose(); // refused before the take existed; no clipboard change yet: BeginLive was not called
            return;
        }
        live.Target = _take!.PasteTarget;
        _paster.BeginLive(_settings.RestoreClipboard);
        live.StartRetryTimer();
        var targetName = live.Target == IntPtr.Zero ? "none" : FocusedTextContext.ProcessName(live.Target) ?? "unknown";
        Core.Log.Info($"LIVE-DICTATE: started (target {targetName})");
        if (live.Target == IntPtr.Zero)
            Core.Log.Info("LIVE-DICTATE: no target window captured; pieces will be held");
        else if (NativeMethods.GetWindowThreadProcessId(live.Target, out uint targetPid) != 0 && targetPid == (uint)Environment.ProcessId)
            Core.Log.Info("LIVE-DICTATE: target is a window of this app; pieces will be held");
    }

    private async Task<string> DecodeLivePieceAsync(byte[] wav, Core.PunctuationMode mode, CancellationToken ct)
    {
        if (_fakeGemini) return "test";
        // No TextCleanup.ForDictation per piece: it would capitalise and end every piece with a period.
        if (mode != Core.PunctuationMode.SpokenOnly)
            return Backstops(Core.TranscribePurpose.LivePiece, Core.LiveDictation.FinishPiece(await _offline.TranscribeAsync(wav, ct), mode));
        // "Nur gesprochen": Parakeet's marks stay only where the token timings show speech time behind them (Mac #339/#347).
        var (text, timed) = await _offline.TranscribeSpokenMarksAsync(wav, ct);
        return Backstops(Core.TranscribePurpose.LivePiece, Core.LiveDictation.FinishPiece(text, mode, timedMarks: timed));
    }

    private Task ProcessLiveAsync(Take take)
    {
        var live = take.Live!;
        var audio = take.Audio!;
        take.Audio = null;
        byte[]? wav = null;
        double seconds = 0;
        var cancelledAction = LiveClipboardAtEnd(null, false);

        return ProcessTakeAsync(take, "Dictate (live)", Core.UserCopy.LiveFinishing, Core.FailureContext.Live,
            failed: () => wav == null ? null : take.ToReplay(wav, seconds, null),
            // Live differs from the other runs here on purpose: the session is cancelled and the clipboard settled
            // (EndLive) after the toast, and a cancel keeps no audio for the tray menu.
            onCancelled: () => _paster.EndLive(cancelledAction, null),
            onFailed: () =>
            {
                live.Session.Cancel();
                _paster.EndLive(cancelledAction, null);
            },
            body: async ct =>
            {
                wav = await audio.StopAsync();
                seconds = Core.Wav.DurationSeconds(wav);
                Core.Log.Info($"Dictate (live): recorded {seconds:0.0} s");

                // A test file arrives whole: cut it like a recording would have been.
                if (audio is not MicAudioSource && Core.Wav.TryGetPcm16(wav, out var pcm) && pcm.SampleRate == 16000 && pcm.Channels == 1)
                    live.FeedWhole(wav.AsSpan(pcm.DataOffset, pcm.DataLength));
                live.EndRecording();

                using var registration = ct.Register(live.Session.Cancel);
                var outcome = await live.FinishAsync(wav);

                string fallback = "";
                if (outcome.FailedPieces > 0)
                {
                    try
                    {
                        fallback = Core.TextCleanup.ForDictation(Backstops(Core.TranscribePurpose.LivePiece, await _engines.Local.TranscribeAsync(wav, Core.PunctuationMode.Automatic, ct)));
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
                Core.Presentation? shown;
                if (action == Core.LiveClipboardEnd.KeepFallback)
                {
                    shown = Core.Presentation.Notice(Core.UserCopy.LiveTitle,
                        "Ein Teil des Diktats konnte nicht live eingefügt werden. Der vollständige Text liegt in der Zwischenablage.",
                        Core.Severity.Warning);
                }
                else if (outcome.FailedPieces > 0)
                {
                    // Nothing to put on the clipboard: keep the take for a retry (and the crash checkpoint).
                    keepCheckpoint = true;
                    shown = Core.Presentation.Notice(Core.UserCopy.LiveTitle, "Ein Teil des Diktats konnte nicht erkannt werden. Bitte den Text prüfen.",
                        Core.Severity.Error, Core.FollowUp.Retry, keepAudio: true);
                }
                else if (outcome.HeldText.Length > 0)
                {
                    shown = Core.Presentation.Notice(Core.UserCopy.LiveTitle,
                        "Das Ende des Diktats wurde nicht eingefügt, weil ein anderes Fenster vorn war. Es liegt in der Zwischenablage: Strg+V drücken.",
                        Core.Severity.Info);
                }
                else if (outcome.Text.Length == 0)
                {
                    shown = Core.Presentation.Notice(Core.UserCopy.NoSpeechTitle, Core.UserCopy.NoSpeechText, Core.Severity.Info);
                }
                else
                {
                    shown = Core.Presentation.DonePill(Core.UserCopy.LiveInserted);
                    _lastLive.Remember(outcome.Text, live.TargetApp);
                }

                if (finalText.Length > 0) _recent.Add(finalText);
                if (outcome.FailedPieces == 0) _lastFailed = null;
                if (!keepCheckpoint) Core.RecordingCheckpoint.DiscardAll();

                if (outcome.FailedPieces == 0 && outcome.Text.Length > 0 && !_fakeGemini)
                    _ = MeasureLiveDriftAsync(outcome.Text, wav);
                return shown;
            });
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
            if (!IsIdle)
            {
                Core.Log.Info("LIVE-DICTATE: drift measurement skipped, the app is busy");
                return;
            }
            var whole = await _engines.Local.TranscribeAsync(wav, Core.PunctuationMode.Automatic, CancellationToken.None);
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
        if (!IsIdle || _lastFailed == null) return;
        var run = _lastFailed;
        Core.Log.Info($"{run.Kind}: retrying the last recording ({run.Seconds:0.0} s)");
        RunAgain(run);
    }

    // Mac: „Beschäftigt“ when a menu row needs the pipeline while a recording or a run is going.
    private bool NotIdleSayBusy()
    {
        if (IsIdle) return false;
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
        RunAgain(new FailedRun(kept.Wav, Core.RecordingKindName.Dictate, kept.Seconds, StopKey: _settings.DictateHotkey));
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
        RunAgain(new FailedRun(kept.Wav, Core.RecordingKindName.Prompt, kept.Seconds, StopKey: _settings.PromptHotkey, NoNewTextTemplate: true));
    }

    /// <summary>Runs kept audio through the pipeline from idle (retry, cancelled recording, without template).</summary>
    private void RunAgain(FailedRun run)
    {
        // The screenshot may be switched off or offline since. Nothing of the old recording is left to clean up: every
        // take ends in Dispose, which also drops its late-copy watch.
        var take = _take = Take.Replay(run, _paster, Core.ScreenshotPolicy.Allowed(_settings));
        _ = ProcessAsync(take, run);
    }

    /// <param name="take">The run: a recording to stop (<paramref name="retry"/> null) or a replay of <paramref name="retry"/>.</param>
    private Task ProcessAsync(Take take, FailedRun? retry)
    {
        var audio = take.Audio;
        take.Audio = null;
        byte[]? wav = null;
        double seconds = 0;
        string? selection = null;
        var busy = take.QuickAction != null ? Core.UserCopy.QuickActionBusy(take.QuickAction)
            : take.Mode == Mode.Dictate ? Core.UserCopy.Transcribing
            : take.Mode == Mode.Template && take.Template != null ? Core.UserCopy.TemplateBusy(take.Template.DisplayName)
            : Core.UserCopy.RecognizingInstruction;

        return ProcessTakeAsync(take, take.Mode.ToString(), busy, Core.FailureContext.Dictation,
            // What a retry needs; null when the audio itself is lost (then there is nothing to try again).
            failed: () => wav == null ? null : take.ToReplay(wav, seconds, selection),
            // Mac: a cancel is one keystroke and can be an accident; keep a dictation for the tray menu (only dictation:
            // an instruction run again as dictation would paste the instruction).
            onCancelled: () =>
            {
                if (take.Mode == Mode.Dictate && wav is { Length: > 0 })
                {
                    _cancelled.Keep(wav, seconds);
                    Core.Log.Info($"Dictate: kept the cancelled recording ({seconds:0.0} s) for the tray menu");
                }
            },
            onFailed: null,
            body: async ct =>
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
                    catch (Core.AudioSilentException) when (take.QuickAction != null || take.Mode == Mode.Template)
                    {
                        // Text marked, Ctrl+1 pressed without a word (or a template run on a selection): the selection alone is the input.
                        // A silent template run with nothing marked is still a microphone problem.
                        if (take.QuickAction == null && take.Selection != null) selection = await take.Selection;
                        if (take.QuickAction == null && !Core.TemplatePrompt.AcceptsEmptyTranscript(selection)) throw;
                        wav = Array.Empty<byte>();
                    }
                    seconds = wav.Length == 0 ? 0 : Core.Wav.DurationSeconds(wav);
                    Core.Log.Info($"{take.Mode}: recorded {seconds:0.0} s");
                    // A quick instruction needs no speech: mark text, press the hotkey and right away Ctrl+1.
                    // A template may format a selection with no speech, so it needs the selection before it can judge "too short".
                    if (take.Mode == Mode.Template && take.Selection != null && selection == null) selection = await take.Selection;
                    if (seconds < 0.5 && take.QuickAction == null
                        && !(take.Mode == Mode.Template && Core.TemplatePrompt.AcceptsEmptyTranscript(selection)))
                    {
                        // A double press, not a recording: say so instead of a misleading microphone error.
                        Core.RecordingCheckpoint.DiscardAll();
                        return Core.Presentation.Notice("Aufnahme zu kurz", "Kürzel drücken, sprechen, dann erneut drücken.", Core.Severity.Info);
                    }
                    if (take.Mode == Mode.Prompt && take.Selection != null) selection = await take.Selection;
                }

                string text = take.Mode switch
                {
                    Mode.Dictate => await DictateAsync(wav, seconds, ct),
                    Mode.Template => await TemplateAsync(wav, seconds, selection, retry != null, ct),
                    _ => await PromptAsync(wav, seconds, selection, retry != null, ct),
                };
                ct.ThrowIfCancellationRequested();

                if (text.Length == 0)
                {
                    Core.RecordingCheckpoint.DiscardAll();
                    if (take.Mode == Mode.Dictate && _welcomeForm is { TakesTryResult: true } silentTour && take.PasteTarget == silentTour.Handle) silentTour.ShowTryResult("");
                    return null; // already reported
                }
                _lastFailed = null; // this run worked; the old failure is history
                Core.RecordingCheckpoint.DiscardAll();
                _recent.Add(text);
                // Mac: a template shaped this dictation; for 10 minutes the same audio can run without it.
                if (take.Mode == Mode.Template && wav is { Length: > 0 } && take.Template != null)
                {
                    _templateReplay.Keep(wav, seconds, take.Template.DisplayName, DateTime.UtcNow);
                    _replayExpiry.Start();
                }

                // The tour's „Ausprobieren“ step started this dictation in its own window: show the text there.
                if (take.Mode == Mode.Dictate && retry == null && _welcomeForm is { TakesTryResult: true } tour
                    && take.PasteTarget != IntPtr.Zero && take.PasteTarget == tour.Handle)
                {
                    _paster.KeepOnClipboard(text);
                    tour.ShowTryResult(text);
                    return Core.Presentation.DonePill("Es funktioniert");
                }

                // A quick-action rewrite that lost most of a long selection must not silently replace it. Spoken
                // instructions are unknown here (Gemini hears them) and "fass zusammen" is a normal request, so
                // they rely on the MAX_TOKENS check alone.
                if (take.Mode == Mode.Prompt && selection != null && take.QuickAction != null
                    && Core.ReplyGuards.AnswerSuspiciouslyShort(selection.Length, text.Length, take.QuickAction))
                {
                    Core.Log.Info($"Prompt: answer {text.Length} chars for a selection of {selection.Length}, not pasted");
                    if (!_paster.KeepOnClipboard(text)) return Core.RunOutcomes.ForPaste(Core.PasteOutcome.Failed, false);
                    return Core.Presentation.Notice("Bitte prüfen", "Antwort deutlich kürzer als die Markierung – liegt in der Zwischenablage, bitte prüfen.",
                        Core.Severity.Warning) with { ErrorSound = true };
                }

                // Like the Mac since 8.75: a retry runs from idle (tray menu or balloon click, recovered recording);
                // the window in front then is whatever the user did since, so copy only.
                var keepOnly = !Core.ClipboardPasteGuard.MayAutoPaste(_settings.AutoPaste, clipboardOnlyRun: take.ClipboardOnly);
                var outcome = keepOnly
                    ? (_paster.KeepOnClipboard(text) ? Core.PasteOutcome.ClipboardOnlyByChoice : Core.PasteOutcome.Failed)
                    : await _paster.PasteAsync(text, take.PasteTarget, _settings.ClipboardAfterPaste, ct);
                Core.Log.Info($"{take.Mode}: {outcome}, {text.Length} chars, autoPaste {(_settings.AutoPaste ? "on" : "off")}, after paste {Core.Choices.AfterPaste.Token(_settings.ClipboardAfterPaste)}");
                return Core.RunOutcomes.ForPaste(outcome, take.Unsupported.Count > 0);
            });
    }

    /// <summary>
    /// The one skeleton of a run that has stopped recording (dictation, prompt, template, live): request, processing pill,
    /// the shared failure ladder (<see cref="Core.RunOutcomes.ForFailure"/>), one <see cref="Present"/>, and the take's end.
    /// <paramref name="body"/> returns what to tell the user (null: it already did). A cancelled take keeps nothing but what
    /// <paramref name="onCancelled"/> keeps; <paramref name="onFailed"/> runs after a failure was shown.
    /// </summary>
    private async Task ProcessTakeAsync(Take take, string label, string busyStatus, Core.FailureContext context,
        Func<FailedRun?> failed, Action? onCancelled, Action? onFailed, Func<CancellationToken, Task<Core.Presentation?>> body)
    {
        take.Cts = new CancellationTokenSource();
        var ct = take.Cts.Token;
        take.BusyStatus = busyStatus;
        take.Phase = TakePhase.Processing;
        RefreshStatus();
        _overlay.ShowProcessing(take.BusyStatus);
        Sounds.Stop();
        bool done = false;

        try
        {
            if (await body(ct) is { } shown) done = Present(shown, take, failed());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The pill disappearing is the feedback; a balloon on top of it would be noise.
            Core.Log.Info($"{label}: cancelled by user");
            onCancelled?.Invoke();
            Core.RecordingCheckpoint.DiscardAll();
        }
        catch (Exception e)
        {
            var line = Core.RunOutcomes.LogLine(e, out var withException);
            if (line != null) Core.Log.Error($"{label}: {line}", withException ? e : null);
            var lost = failed();
            Present(Core.RunOutcomes.ForFailure(e, audioKept: lost != null, context), take, lost);
            onFailed?.Invoke();
        }
        finally
        {
            // An early end must stop the selection copy still polling and its late-copy watch. Safe after a paste: the
            // paste cancelled the watch itself, and the clipboard restore is separate (ClipboardPaster._pendingRestore).
            // A failed run keeps its screenshot in _lastFailed for the retry.
            EndTake(take);
            if (!done) _overlay.HideOverlay();
            RefreshStatus();
        }
    }

    /// <summary>
    /// Shows one <see cref="Core.Presentation"/>: the pill, the warning card or a toast with its sound and follow-up.
    /// <paramref name="failed"/> is what a retry would run. True when the overlay shows a result (it must not be hidden).
    /// </summary>
    private bool Present(Core.Presentation p, Take take, FailedRun? failed)
    {
        switch (p.Kind)
        {
            case Core.PresentationKind.Done:
                _overlay.ShowDone(p.Text);
                return true;
            case Core.PresentationKind.Warning:
                _overlay.ShowWarning(take.Unsupported, p.Pasted);
                return true;
            case Core.PresentationKind.Toast:
                if (p.ErrorSound) Sounds.Error();
                if (p.KeepAudio) _lastFailed = failed;
                var url = p.Url;
                var (onClick, label) = p.Action switch
                {
                    // The audio is gone: nothing to run again.
                    Core.FollowUp.Retry when failed != null => ((Action?)(() => RetryLast()), Core.UserCopy.ActionRetry),
                    Core.FollowUp.OpenSettings => ((Action?)ShowSettings, Core.UserCopy.ActionOpenSettings),
                    Core.FollowUp.OpenAccounts => ((Action?)(() => ShowSettingsPage(SettingsForm.AccountsPage)), Core.UserCopy.ActionOpenSettings),
                    Core.FollowUp.TopUp when url != null => ((Action?)(() => OpenBillingUrl(url)), Core.UserCopy.ActionTopUp),
                    _ => ((Action?)null, (string?)null),
                };
                Notify(p.Title, p.Text, Toast.IconOf(p.Icon), onClick, label);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Opens the provider's billing page from the "Guthaben" balloon.</summary>
    private static void OpenBillingUrl(string url)
    {
        if (!Core.NetworkPolicy.IsAllowedBrowserUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening the billing page failed: {e.GetType().Name}"); }
    }

    private async Task<string> DictateAsync(byte[] wav, double seconds, CancellationToken ct)
    {
        var punctuation = PunctuationFor(Core.TranscribePurpose.Dictation);
        string raw = _fakeGemini
            ? Backstops(Core.TranscribePurpose.Dictation, "das ist ein test diktat aus der virtuellen maschine")
            : await TranscribeAsync(Core.TranscribePurpose.Dictation, wav, ct);
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
        ThrowIfLateCopy("Prompt", selection, isRetry);
        Core.Log.Info($"Prompt: {(selection == null ? "no selection (compose)" : $"selection {selection.Length} chars")}");

        string systemPrompt = Core.DictatePromptText.BuildSystemPrompt(_settings.PromptModeSystemPrompt,
            _take!.NoNewTextTemplate ? null : _settings.DictatePromptTemplate) ?? "";
        if (_take!.QuickAction is { } quick) return await QuickActionAsync(quick, wav, seconds, selection, systemPrompt, ct);

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
        var answer = _gemini.PromptAsync(wav, selection, systemPrompt, ct, HistoryFor(selection), _take!.Shot);

        string? instruction = null;
        if (selection == null)
        {
            // Nothing selected and the instruction is only "korrigiere" / "mach das freundlicher":
            // there is nothing to edit, and the model would invent a text. Transcribe in parallel to check.
            instruction = await TranscribeAsync(Core.TranscribePurpose.Instruction, wav, ct);
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
        var template = _take!.Template ?? throw new InvalidOperationException("Template run without a template");
        // Same guard as Dictate Prompt: a late Ctrl+C means a selection we never read.
        ThrowIfLateCopy("Template", selection, isRetry);

        if (wav.Length > 0 && seconds >= 1.0) SetBusy(Core.UserCopy.Transcribing);
        string transcript = wav.Length == 0 || seconds < 1.0 ? ""
            : await TranscribeBodyAsync(Core.TranscribePurpose.TemplateBody, wav, ct);
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
            raw = await PromptProvider().CompleteTextAsync(system, user, _take!.Shot, ct);
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
        string instruction = (await TranscribeAsync(Core.TranscribePurpose.Instruction, wav, ct)).Trim();
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
            : await TranscribeBodyAsync(Core.TranscribePurpose.QuickActionTranscript, wav, ct);
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
    /// with, collects the words nobody dictated (<see cref="Take.Unsupported"/>, shown after the paste).
    /// The template text is NOT a source: example content in it (a drug, a dose) must still be flagged when
    /// nobody dictated it. Only a template's label words pass (<paramref name="templateInstructions"/>).
    /// A template run keeps its Markdown (<paramref name="keepMarkdown"/>), as on the Mac.
    /// </summary>
    private string Reply(string raw, string?[]? sources, string? templateInstructions = null, bool keepMarkdown = false)
    {
        string text = Core.TextCleanup.NormalizeReply(raw);
        if (!keepMarkdown) text = Core.TextCleanup.StripMarkdown(text);
        if (sources == null || text.Length == 0) return text;
        var unsupported = _take!.Unsupported = Core.DictationFidelityCheck.UnsupportedWords(text, sources, templateInstructions);
        // Count only: the words are patient data and stay out of the log.
        if (unsupported.Count > 0) Core.Log.Info($"Prompt: {unsupported.Count} word(s) in the reply are not in the dictation, warning shown");
        return text;
    }

    /// <summary>
    /// Speech recognition for a purpose with the chosen engine: Parakeet on this PC, or Gemini / OpenAI, then the
    /// backstops <see cref="Core.TranscriptionPolicy"/> lists for that purpose and engine.
    /// </summary>
    private async Task<string> TranscribeAsync(Core.TranscribePurpose purpose, byte[] wav, CancellationToken ct)
    {
        var step = StepFor(purpose);
        var engine = _engines.SpeechEngine(step);
        var policy = Core.TranscriptionPolicy.For(purpose, engine);
        var punctuation = policy.Punctuation(_settings.PunctuationMode, engine);
        var raw = engine == Core.Engine.Local
            ? await _engines.Local.TranscribeAsync(wav, punctuation, ct)
            : await TranscribeOnlineAsync(_engines.Speech(step), wav, ct, policy);
        return policy.GlossaryBackstops ? GlossaryBackstops(raw, policy.EchoStrip) : raw;
    }

    /// <summary>The transcript a template or quick instruction works on (test mode: a fixed sentence, no recognizer).</summary>
    private async Task<string> TranscribeBodyAsync(Core.TranscribePurpose purpose, byte[] wav, CancellationToken ct) =>
        _fakeGemini ? "Patient klagt über Kopfschmerzen seit drei Tagen" : await TranscribeAsync(purpose, wav, ct);

    private static EngineStep StepFor(Core.TranscribePurpose purpose) =>
        purpose == Core.TranscribePurpose.MeetingChunk ? EngineStep.Meeting : EngineStep.Dictation;

    /// <summary>The engine whose recognizer the purpose uses (Live diktieren always decodes on this PC).</summary>
    private Core.Engine SpeechEngineFor(Core.TranscribePurpose purpose) =>
        purpose == Core.TranscribePurpose.LivePiece ? Core.Engine.Local : _engines.SpeechEngine(StepFor(purpose));

    /// <summary>The punctuation mode the purpose transcribes with (the stored one only where the policy takes it, on this PC).</summary>
    private Core.PunctuationMode PunctuationFor(Core.TranscribePurpose purpose)
    {
        var engine = SpeechEngineFor(purpose);
        return Core.TranscriptionPolicy.For(purpose, engine).Punctuation(_settings.PunctuationMode, engine);
    }

    /// <summary>The glossary backstops the purpose's policy asks for, on text that was not transcribed here (a live piece, the test sentence).</summary>
    private string Backstops(Core.TranscribePurpose purpose, string raw)
    {
        var policy = Core.TranscriptionPolicy.For(purpose, SpeechEngineFor(purpose));
        return policy.GlossaryBackstops ? GlossaryBackstops(raw, policy.EchoStrip) : raw;
    }

    /// <summary>
    /// A Ctrl+C that came after the 2 s deadline means there is a selection we never read: writing a new text now
    /// would replace it. (A retry has its own, finished selection.)
    /// </summary>
    private void ThrowIfLateCopy(string what, string? selection, bool isRetry)
    {
        if (selection != null || isRetry || !_paster.LateCopyArrived) return;
        Core.Log.Info($"{what}: selection arrived after the deadline, not composing over it");
        throw new Core.SelectionUnreadableException();
    }

    /// <summary>The text provider of Dictate Prompt, templates and quick instructions; says "loading" while the local model is not up yet.</summary>
    private Core.ITextProvider PromptProvider()
    {
        if (_settings.EffectiveTextEngine == Core.Engine.Local && !_offline.LlmReady)
            SetBusy(Core.UserCopy.LoadingLlm);
        return _engines.Text(EngineStep.Prompt)!;
    }

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
        return PromptProvider().PromptTextAsync(selection, instruction, systemPrompt, ct, history, _take!.Shot);
    }

    /// <summary>
    /// Gemini or OpenAI. Gemini is cut above 2.5 min, OpenAI above 11 min (25 MB upload limit).
    /// Pieces are cut in pauses, four at a time: one request would hit Gemini's inline size limit
    /// and take as long as all of it. A piece that comes back as nothing but glossary words (the
    /// model filled a quiet stretch with vocabulary) is asked again once without the glossary, like the Mac.
    /// With <see cref="Core.TranscriptionPolicy.SpeechGate"/> every piece also passes the Silero VAD gate: no speech skips the
    /// request, a long quiet head or tail is cut off (the energy gate above stays first).
    /// </summary>
    private Task<string> TranscribeOnlineAsync(Core.ISpeechProvider speech, byte[] wav, CancellationToken ct, Core.TranscriptionPolicy policy)
    {
        var glossary = _settings.Glossary.ToList();
        var echoTerms = Core.GlossaryPrompt.EchoTerms(glossary);
        return Core.ChunkedTranscription.TranscribeAsync(
            speech.SplitForUpload(wav),
            async (piece, c) =>
            {
                // Nothing but room noise: Gemini would answer with the glossary. Also saves the request.
                if (!HasSpeech(piece)) return "";
                var chunk = piece;
                if (policy.SpeechGate)
                {
                    // Silero decides next: null = no speech, a copy = quiet ends cut off, same array = unchanged
                    // (also while the model is not loaded yet, or when anything about it fails).
                    var gated = Core.SpeechActivityGate.Apply(piece);
                    if (gated == null) return "";
                    chunk = gated;
                }
                // Live-meeting chunks carry loud system audio that gpt-transcribe answers with the keywords themselves.
                var text = await speech.TranscribeAsync(chunk, glossary, policy.SendKeywords, c);
                if (text.Trim().Length == 0
                    || Core.TextCleanup.DiscardGlossaryEcho(text, Core.Wav.DurationSeconds(chunk), echoTerms).Length > 0)
                    return text;
                Core.Log.Info("Dictate: glossary echo discarded, asking again without the glossary");
                return await speech.TranscribeAsync(chunk, Array.Empty<string>(), c);
            }, maxParallel: 4, ct);
    }

    /// <summary>
    /// Silero VAD for the cloud speech gate: fetched and loaded in the background, only with a cloud speech
    /// engine (Mac: FullApp, utility priority). Runs once per launch; a dictation never waits for it, until
    /// it is ready the recording uploads as before.
    /// </summary>
    private void PrepareSpeechGate()
    {
        if (_fakeGemini || _settings.EffectiveSpeechEngine == Core.Engine.Local) return;
        _offline.PrepareSpeechGate();
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

    // ---------------- live meeting ----------------

    /// <summary>
    /// Why a meeting cannot start (or its backlog cannot be retried) with the current engines, or null.
    /// Offline works: Parakeet transcribes meeting chunks like long dictations.
    /// </summary>
    private string? MeetingUnavailable()
    {
        if (_fakeGemini) return null;
        var ready = _engines.Check(EngineStep.Meeting);
        if (ready.SpeechModelMissing)
            return "Die Offline-Spracherkennung ist noch nicht geladen. In den Einstellungen unter „Offline-Modelle“ laden.";
        if (ready.MissingKey is { } provider)
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
        string raw = await TranscribeAsync(Core.TranscribePurpose.MeetingChunk, wav, ct);
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
        if (!IsIdle || _glossaryBusy || (_readAloud?.IsActive == true && !_readAloud.IsSpeaking))
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
                    _appliedSettings = Core.SettingsDelta.Snapshot.Of(_settings); // reloaded just now: the next apply must not reload again
                    _settingsForm?.Pull(); // an open window shows the term (and keeps one it is editing)
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
        catch (Core.SelectionUnreadableException)
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
        if (IsIdle) _overlay.ShowDone(title, neutral);
        else Notify("WhisperShortcut", title, ToolTipIcon.Info);
    }

    /// <summary>The local models the mode's engines need are on disk (true when it uses none).</summary>

    // ---------------- UI ----------------

    /// <summary>
    /// Esc and Ctrl+1–9 belong to the other programs except while we need them: Esc while recording or processing,
    /// Ctrl+1–9 only while a Dictate Prompt recording runs.
    /// </summary>
    private void SyncSessionHotkeys()
    {
        if (IsIdle) _hotkeys.Unregister(Escape);
        else if (!_hotkeys.IsRegistered(Escape)) _hotkeys.Register(Escape, OnEscape);
        bool quick = _take is { Phase: TakePhase.Recording, Kind: Core.RecordingKindName.Prompt };
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

    /// <summary>Hotkeys, tray icon and status text follow the take (none = idle). Call after every change of <c>_take</c> or its phase.</summary>
    private void RefreshStatus()
    {
        SyncSessionHotkeys();
        SyncGlobalHotkeys();
        (string status, Icon icon) = _take switch
        {
            { Phase: TakePhase.Recording } t => (Core.UserCopy.RecordingStatus(Hotkey.Display(t.StopKey), prompt: t.Mode != Mode.Dictate), TrayIcons.Recording),
            { Phase: TakePhase.Processing } => (TrayProcessingStatus(), TrayIcons.Processing),
            _ when _readAloud?.IsActive == true => (_readAloud.StatusText, _readAloud.IsSpeaking ? TrayIcons.Ready : TrayIcons.Processing),
            _ when MissingKey => ("Kein API-Schlüssel eingetragen", TrayIcons.Warning),
            _ when _engines.Check(EngineStep.Dictation, checkKeys: false).ModelsMissing => ("Spracherkennung noch nicht geladen", TrayIcons.Warning),
            _ when _settings.OfflineMode => ("Bereit (offline, nichts verlässt diesen PC)", TrayIcons.Ready),
            _ => (Core.UserCopy.ReadyStatus, TrayIcons.Ready),
        };
        _icon.Icon = icon;
        SetStatusText(status);
        if (IsIdle && _updateNoticePending)
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
        if (IsIdle) ShowUpdateNotice();
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
        // (focus-steal prevention) stays "active" while the user works elsewhere. The window itself in
        // front counts as "focused" for the settings window; one in the background must not keep the hotkeys off.
        // Only a focused hotkey field turns them off (it records the combination); everywhere else they work.
        bool recordingHotkeyField = f is { IsDisposed: false, Visible: true } && ClipboardPaster.ForegroundRoot() == f.Handle && f.RecordingHotkey;
        bool wanted = Core.HotkeyPolicy.WantGlobalHotkeys(recordingHotkeyField, IsIdle);
        // Keys saved during a recording (Saved left them pending) are taken over once it is idle again.
        bool pendingWhileOn = wanted && _globalHotkeysOn && _hotkeyCheckPending && IsIdle;
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
            Core.Log.Info("Hotkeys off: hotkey field in the settings window has the focus");
        }
    }

    private void SetBusy(string status)
    {
        if (_take is { Phase: TakePhase.Processing } take)
        {
            take.BusyStatus = status;
            SetStatusText(TrayProcessingStatus());
            _overlay.ShowProcessing(status);
        }
    }

    /// <summary>Mac AppState processing lines: the tray says what runs, the pill shows the step (<see cref="Take.BusyStatus"/>).</summary>
    private string TrayProcessingStatus() => _take is { Mode: Mode.Dictate, QuickAction: null }
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
        // Re-evaluate the global shortcuts when the tour comes to the front (see SyncGlobalHotkeys).
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
    private void AfterTourSaved() => ApplySettingsChange(TakeSettingsDelta(), fromSettingsWindow: false);

    // What the follow-up steps last saw. The settings window, the tour and MCP all write into Settings before they
    // tell us, so the "before" of a change is this snapshot, refreshed by every apply.
    private Core.SettingsDelta.Snapshot _appliedSettings = null!;

    private Core.SettingsDelta TakeSettingsDelta()
    {
        var now = Core.SettingsDelta.Snapshot.Of(_settings);
        var d = Core.SettingsDelta.Between(_appliedSettings, now);
        _appliedSettings = now;
        return d;
    }

    /// <summary>
    /// The one list of follow-ups after settings changed, for the settings window, the tour and MCP. Steps that
    /// guard themselves (state, prewarm, screenshot, sounds, offline stop) run every time; the others run on their flag.
    /// <paramref name="fromSettingsWindow"/>: that window applies after every edit, even unrelated ones, so it keeps
    /// its old reactions to the current state (clear memory while offline, check the hotkeys when it leaves the front)
    /// instead of reacting to changes only.
    /// </summary>
    private void ApplySettingsChange(Core.SettingsDelta d, bool fromSettingsWindow)
    {
        // Only a changed glossary reloads the offline recognizer (a few seconds); most applies are other edits.
        if (d.GlossaryChanged) _offline.GlossaryChanged();
        Sounds.Enabled = _settings.Sounds;
        if (fromSettingsWindow)
        {
            _hotkeyCheckPending = true;
            // The hotkeys are on unless a hotkey field has the focus (the save runs 500 ms after the last edit, so the
            // field may already be left): then they were registered from the old values and nothing else would re-arm them.
            // Only while idle: during a recording the key that started it must keep stopping it (the check runs later).
            if (_globalHotkeysOn && IsIdle)
            {
                RegisterHotkeys(notify: true);
                _hotkeyCheckPending = false;
            }
        }
        else if (d.HotkeysChanged && _globalHotkeysOn) RegisterHotkeys(); // otherwise they register when the hotkey field loses the focus
        if (_settings.OfflineMode && (fromSettingsWindow || d.OfflineModeChanged)) _promptMemory.Clear();
        RefreshStatus(); // key may have been added: tooltip "Bereit" instead of "Kein API-Schlüssel"
        _offline.Prewarm(); // offline mode or the engine may just have been switched to this PC
        PrepareSpeechGate(); // ... or away from it
        DropScreenshotIfNotAllowed(); // a local text engine must not get a held screenshot later
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
    /// The global hotkeys are off only while a hotkey field of the window has the focus; elsewhere in the window the
    /// key arrives here, and SettingsForm also closes itself on it as a fallback.
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
    /// The timer keeps ticking and keeps the spare current: a change from an AI app, the chat or the tray makes it
    /// stale, and the next idle moment rebuilds it (a stale one would be found only at the next open, which then builds synchronously).
    /// </summary>
    private void ScheduleSpareSettings(int delayMs = 3000)
    {
        _spareTimer?.Dispose();
        _spareTimer = new System.Windows.Forms.Timer { Interval = delayMs };
        _spareTimer.Tick += (_, _) =>
        {
            // Busy (recording, a window open): try again later; the build blocks the UI thread for a moment.
            if (!IsIdle || _settingsForm != null || _welcomeForm is { IsDisposed: false }) return;
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
        // Computed once per tick (JSON plus hashes); the timer runs for the whole session.
        var fingerprint = _settings.Fingerprint();
        if (_spareSettings is not { IsDisposed: false } || _spareFingerprint != fingerprint)
        {
            _spareSettings?.Dispose();
            _spareSettings = new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, _mcp);
            _spareSettings.Prewarm();
            _spareFingerprint = fingerprint;
            Core.Log.Info($"Settings window built in advance in {watch.ElapsedMilliseconds} ms");
            return; // the chat, if still needed, on the next tick
        }
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
        var shown = System.Diagnostics.Stopwatch.StartNew();
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
        if (!IsIdle)
        {
            Notify("Aufnahme läuft", "Die Einstellungen gehen erst danach auf. Bitte die Aufnahme beenden, "
                + "warten, bis der Text da ist, und dann noch einmal klicken.", ToolTipIcon.Info);
            return;
        }

        // The global hotkeys are off only while a hotkey field has the focus, so it can record Ctrl+Shift+D itself.
        // Everywhere else (other fields, behind Word, minimized) they work as usual, e.g. dictating into the window.
        var spare = TakeSpareSettings();
        var form = _settingsForm = spare ?? new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, _mcp);
        // Activation messages can still arrive after FormClosed: only the open window may re-evaluate.
        _settingsForm.Activated += (_, _) => { if (_settingsForm == form) SyncGlobalHotkeys(); };
        // Posted: while Deactivate runs the new foreground window is not set yet.
        _settingsForm.Deactivate += (_, _) => { if (_settingsForm == form) _ui.Post(_ => SyncGlobalHotkeys(), null); };
        form.HotkeyFieldFocusChanged += () => { if (_settingsForm == form) _ui.Post(_ => SyncGlobalHotkeys(), null); };
        // Edits apply by themselves (shortly after each one): apply right away, check the hotkeys once the window leaves the front.
        _settingsForm.Saved += () => ApplySettingsChange(TakeSettingsDelta(), fromSettingsWindow: true);
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
        Core.Log.Info($"Settings window shown in {shown.ElapsedMilliseconds} ms ({(spare != null ? "built in advance" : "built now")})");
    }

    /// <summary>
    /// An AI app changed the settings (directly, through an accepted suggestion, or an Undo): apply them like
    /// „Speichern“ does. An open settings window takes the new values into every field the user is not editing
    /// (<see cref="SettingsForm.Pull()"/>); a field being edited keeps the edit, so nothing is lost and nothing refused.
    /// </summary>
    private void AfterMcpApply()
    {
        ApplySettingsChange(TakeSettingsDelta(), fromSettingsWindow: false);
        _settingsForm?.Pull();
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
        if (_take is { } running)
        {
            running.KeepCheckpoint = true; // a recording cut off by quitting stays recoverable on the next start
            running.Dispose();
        }
        _icon.Visible = false;
        _icon.Dispose();
        _keyUpTimer.Dispose();
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
