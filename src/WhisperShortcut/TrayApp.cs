using System.Diagnostics;

namespace WhisperShortcut;

internal enum Mode { Dictate, Prompt, Template }

/// <summary>
/// Tray icon, hotkeys and the recording state machine. Same toggle behaviour as the Mac:
/// 1st press starts, 2nd press stops and processes, a press while processing cancels.
/// Everything runs on the UI thread; no window ever takes focus during a recording.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private enum State { Idle, Recording, Processing }

    /// <summary>
    /// A recording whose processing failed for a reason a second try can fix (network, quota, local model).
    /// Also used for a take recovered after a crash. Dropped after the next success.
    /// <see cref="ClipboardOnly"/> is set for a recovered take: the original window is gone.
    /// </summary>
    private sealed record FailedRun(byte[] Wav, Mode Mode, double Seconds, string? Selection, string? QuickAction, IntPtr Target,
        Core.DictationTemplate? Template = null, string StopKey = "", bool ClipboardOnly = false,
        Core.ScreenshotImage? Screenshot = null);

    private const string RetryHint = " Hier klicken, um es noch einmal zu versuchen.";

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
    private readonly ToolStripMenuItem _dictateInfo;
    private readonly ToolStripMenuItem _promptInfo;
    private readonly ToolStripMenuItem _glossaryInfo;
    private readonly ToolStripMenuItem _templateInfo;
    private readonly ToolStripMenuItem _copyLast;
    private readonly ToolStripMenuItem _recentMenu;
    private readonly ToolStripMenuItem _retryItem;
    private readonly RecordingOverlay _overlay = new();
    // In memory only: in a practice a dictation must not stay behind in a file.
    private readonly Core.RecentTexts _recent = new();
    private static readonly Hotkey Escape = new(0, Keys.Escape);
    // Ctrl+1–5 while a Dictate Prompt recording runs: apply a quick instruction (Settings.QuickActions).
    private static readonly Hotkey[] QuickKeys =
        Enumerable.Range(0, Core.QuickActionList.Max).Select(i => new Hotkey(NativeMethods.MOD_CONTROL, Keys.D1 + i)).ToArray();

    private State _state = State.Idle;
    private Mode _mode;
    // A copy taken at the start of a template run: editing the settings meanwhile must not change this run.
    private Core.DictationTemplate? _template;
    // The stored hotkey that started the run; shown in the overlay hint and the status text.
    private string _stopKey = "";
    private Core.IAudioSource? _audio;
    private Task<string?>? _selection;
    private CancellationTokenSource? _selectionCts;
    private string? _quickAction;
    // The window in front when the recording started: the only paste target, for a retry too.
    private IntPtr _pasteTarget;
    private FailedRun? _lastFailed;
    private IReadOnlyList<string> _unsupported = Array.Empty<string>();
    private CancellationTokenSource? _cts;
    private SettingsForm? _settingsForm;
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
    private readonly System.Windows.Forms.Timer _keyUpTimer = new() { Interval = 30 };
    private Keys _armedKey;
    private long _armedKeyDeadline;
    private readonly ToolStripMenuItem _offlineItem;
    private readonly SynchronizationContext _ui;
    // Screenshot hotkey: the image waits here for the next Dictate Prompt or template run (memory only, 5 min).
    private readonly Core.ScreenshotHold _heldShot = new(() => DateTime.UtcNow);
    private readonly System.Windows.Forms.Timer _shotExpiry = new() { Interval = 15_000 };
    private Core.ScreenshotImage? _runShot;
    private CancellationTokenSource? _snipCts;
    private readonly MeetingController _meeting;
    private readonly McpService _mcp;

    public TrayApp(string? testWav, bool fakeGemini)
    {
        _testWav = testWav;
        _fakeGemini = fakeGemini;

        bool firstRun = !File.Exists(Core.Settings.DefaultPath);
        _settings = Core.Settings.Load(Core.Settings.DefaultPath, new DpapiProtector());
        Sounds.Enabled = _settings.Sounds;
        bool testRun = testWav != null || fakeGemini;
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
        _gemini = new Core.GeminiClient(_settings);
        _openAi = new Core.OpenAiClient(_settings);
        _offline = new OfflineEngine(_settings);
        _offline.Prewarm();

        _hotkeys = new HotkeyService();
        _paster = new ClipboardPaster(_hotkeys.Handle);

        _statusItem = new ToolStripMenuItem("Bereit") { Enabled = false };
        _dictateInfo = new ToolStripMenuItem { Enabled = false };
        _promptInfo = new ToolStripMenuItem { Enabled = false };
        _glossaryInfo = new ToolStripMenuItem { Enabled = false };
        _templateInfo = new ToolStripMenuItem { Enabled = false, Visible = false };
        _copyLast = new ToolStripMenuItem("Letzten Text kopieren", null, (_, _) => CopyRecent(_recent.Latest));
        _recentMenu = new ToolStripMenuItem("Letzte Texte");
        _retryItem = new ToolStripMenuItem("Letzte Aufnahme erneut verarbeiten", null, (_, _) => RetryLast()) { Enabled = false };
        // An update restarts the app: not in the middle of a dictation or a meeting.
        _updates = new UpdateService(_settings, () => _state == State.Idle && _meeting?.IsActive != true, Quit);
        _updateItem = new ToolStripMenuItem("Update installieren …", null, (_, _) => _updates.ShowDialog())
        {
            Visible = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        var title = new ToolStripMenuItem("WhisperShortcut") { Enabled = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold), AccessibleName = "WhisperShortcut" };
        _offlineItem = new ToolStripMenuItem("Offline: nichts verlässt den PC", null, (_, _) => ToggleOffline()) { CheckOnClick = false };
        var settingsItem = new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        var logItem = new ToolStripMenuItem("Protokoll öffnen", null, (_, _) => OpenLog());
        var quitItem = new ToolStripMenuItem("Beenden", null, (_, _) => Quit());
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _meeting = new MeetingController(_settings, _ui, TranscribeMeetingChunkAsync, SummarizeMeetingAsync, MeetingUnavailable,
            Notify, TrySave, () => new[] { _settings.DictateHotkey, _settings.PromptHotkey, _settings.GlossaryHotkey,
                    _settings.ScreenshotHotkey, _settings.TemplateHotkey }
                .Concat(_settings.Templates.Select(t => t.Hotkey)).Where(k => k.Length > 0).ToArray(),
            suspend => { if (suspend) { _hotkeys.UnregisterAll(); _globalHotkeysOn = false; } else SyncGlobalHotkeys(notify: true); },
            fakeAudio: testWav != null || fakeGemini);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            title, _statusItem, _updateItem, new ToolStripSeparator(), _dictateInfo, _promptInfo, _glossaryInfo, _templateInfo, new ToolStripSeparator(),
            _meeting.MenuItem, new ToolStripSeparator(),
            _copyLast, _recentMenu, _retryItem, new ToolStripSeparator(),
            _offlineItem, settingsItem, logItem, new ToolStripSeparator(), quitItem,
        });
        menu.Opening += (_, _) =>
        {
            UpdateRecentMenu();
            _retryItem.Enabled = _lastFailed != null && _state == State.Idle;
            _offlineItem.Checked = _settings.OfflineMode;
        };
        UpdateRecentMenu();

        _icon = new NotifyIcon { Icon = TrayIcons.Ready, ContextMenuStrip = menu, Visible = true };
        // One left click opens the settings (Click alone would also fire on right click, which opens the menu).
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _mcp = new McpService(_settings, _ui, () => _state != State.Idle, () => _settingsForm?.HasUnsavedChanges == true, AfterMcpApply);
        _mcp.StartIfEnabled();

        _keyUpTimer.Tick += (_, _) => PollArmedKey();
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

        if (firstRun && !testRun)
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
                "Bitte zuerst den API-Schlüssel eintragen: hier klicken oder auf das blaue Symbol unten rechts.",
                ToolTipIcon.Info, ShowSettings);
        }
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
        _lastFailed = new FailedRun(rec.Wav, mode, rec.Seconds, null, null, IntPtr.Zero,
            StopKey: mode == Mode.Prompt ? _settings.PromptHotkey : _settings.DictateHotkey, ClipboardOnly: true);
        Notify("Aufnahme unterbrochen",
            $"Eine {rec.Seconds:0} s lange Aufnahme war noch nicht fertig. Hier klicken, um sie zu transkribieren. Der Text landet in der Zwischenablage.",
            ToolTipIcon.Warning, RetryLast);
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
        var keys = new List<(string, Action)>
        {
            (_settings.DictateHotkey, () => OnHotkey(Mode.Dictate)),
            (_settings.PromptHotkey, () => OnHotkey(Mode.Prompt)),
            (_settings.GlossaryHotkey, () => _ = AddSelectionToGlossaryAsync()),
        };
        // No meeting hotkey by default: the tray menu starts a meeting.
        if (!string.IsNullOrWhiteSpace(_settings.MeetingHotkey)) keys.Add((_settings.MeetingHotkey, _meeting.Toggle));
        foreach (var (stored, action) in keys)
        {
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, action))
                failed.Add(Hotkey.Display(stored));
        }
        if (_settings.ScreenshotHotkey.Length > 0
            && (!Hotkey.TryParse(_settings.ScreenshotHotkey, out var shotKey) || !_hotkeys.Register(shotKey, OnScreenshotHotkey)))
            failed.Add(Hotkey.Display(_settings.ScreenshotHotkey));

        // Template keys come after the app's own, which win on a clash (see TemplateHotkeys).
        var appHotkeys = new (string Name, string Hotkey)[]
        {
            ("Diktieren", _settings.DictateHotkey),
            ("Text bearbeiten", _settings.PromptHotkey),
            ("Glossar", _settings.GlossaryHotkey),
            ("Screenshot", _settings.ScreenshotHotkey),
            ("Live Meeting", _settings.MeetingHotkey),
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
        _dictateInfo.Text = $"Diktieren: {Hotkey.Display(_settings.DictateHotkey)}";
        _promptInfo.Text = $"Text bearbeiten oder schreiben lassen: {Hotkey.Display(_settings.PromptHotkey)}";
        _glossaryInfo.Text = $"Markierung ins Glossar: {Hotkey.Display(_settings.GlossaryHotkey)}";
        _templateInfo.Text = $"Vorlage anwenden: {Hotkey.Display(_settings.TemplateHotkey)}";
        _templateInfo.Visible = pickerActive;
        // WHY remembered: the settings window closing re-registers with notify, which would repeat the same warning.
        var failedKey = string.Join("|", failed.OrderBy(n => n, StringComparer.Ordinal));
        if (failed.Count == 0) _lastWarnedHotkeys = "";
        else if (notify && failedKey != _lastWarnedHotkeys)
        {
            _lastWarnedHotkeys = failedKey;
            Notify("Tastenkürzel belegt",
                $"{string.Join(" und ", failed)} {(failed.Count > 1 ? "werden" : "wird")} schon von einem anderen Programm benutzt. "
                + "Hier klicken und in den Einstellungen ein anderes wählen.",
                ToolTipIcon.Warning, ShowSettings);
        }
    }

    private void OnHotkey(Mode mode)
    {
        var stored = mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey;
        HandleHotkeyPress(mode, stored, canStopOrCancel: true, () => StartRecording(mode, stored));
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
        if (_state == State.Processing)
        {
            _cts?.Cancel(); // same path as a hotkey press during processing
            return;
        }
        if (_state != State.Recording) return;
        (_audio as MicAudioSource)?.Cancel(discardCheckpoint: true);
        Core.RecordingCheckpoint.DiscardAll();
        _audio = null;
        CancelSelectionCopy(); // no Ctrl+C into the user's window after the cancel
        _runShot = null; // discarded with the recording
        Sounds.Stop();
        _overlay.HideOverlay();
        SetState(State.Idle);
    }

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

    // ---------------- screenshot ----------------

    /// <summary>
    /// Screenshot hotkey: opens the Windows snip UI and holds the image for the next Dictate Prompt or template run.
    /// A second press while the snip UI is open stops waiting. Only when idle: a running recording has its input already.
    /// </summary>
    private void OnScreenshotHotkey()
    {
        Core.Log.Info($"Hotkey Screenshot in state {_state}");
        if (_snipCts != null)
        {
            _snipCts.Cancel();
            return;
        }
        if (_state != State.Idle) return;
        if (!Core.ScreenshotPolicy.Allowed(_settings))
        {
            Sounds.Error();
            Notify("Screenshots sind aus",
                _settings.OfflineMode || _settings.EffectiveTextEngine == Core.Engine.Local
                    ? "Offline geht kein Bild an ein Sprachmodell: Das lokale Modell kann keine Bilder lesen, und nichts soll den PC verlassen."
                    : "Hier klicken und unter „Text bearbeiten“ → „Screenshots mitsenden“ einschalten.",
                ToolTipIcon.Info, ShowSettings);
            return;
        }
        _ = CaptureScreenshotAsync();
    }

    private async Task CaptureScreenshotAsync()
    {
        using var cts = _snipCts = new CancellationTokenSource();
        try
        {
            var image = await ScreenshotCapture.CaptureAsync(_paster, cts.Token);
            if (image == null) return; // cancelled in the snip UI or timed out; the log says which
            _heldShot.Hold(image);
            _shotExpiry.Start();
            Core.Log.Info($"Screenshot held for the next turn ({image.Data.Length / 1024} KB)");
            ShowDoneIfFree("Bildschirmfoto für die nächste Anweisung gemerkt");
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
            Notify("Offline-Modelle fehlen",
                (mode == Mode.Dictate || !OfflineEngine.SpeechInstalled
                    ? "Die Offline-Spracherkennung ist noch nicht geladen."
                    : "Das Sprachmodell für „Text bearbeiten“ ist noch nicht geladen.")
                + " Hier klicken und in den Einstellungen unter „Offline-Modelle“ laden.",
                ToolTipIcon.Warning, ShowSettings);
            return;
        }
        if (mode != Mode.Dictate && _settings.EffectiveTextEngine == Core.Engine.Local && OfflineEngine.LlmUnsupportedCpu)
        {
            Core.Log.Info($"Hotkey {mode} with the local text engine, but this CPU cannot run the language model");
            Sounds.Error();
            Notify("Offline-Sprachmodell", Core.LlamaServerHost.UnsupportedCpuMessage, ToolTipIcon.Warning, ShowSettings);
            return;
        }
        if (MissingKeyFor(mode) is { } provider)
        {
            Core.Log.Info($"Hotkey pressed but no {provider} key (Schlüssel fehlt)");
            Sounds.Error();
            Notify("Schlüssel fehlt", $"Noch kein {provider}-Schlüssel eingetragen. Hier klicken, um ihn einzutragen.",
                ToolTipIcon.Warning, ShowSettings);
            return;
        }

        _mode = mode;
        _stopKey = stored;
        _quickAction = null;
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

        try
        {
            _audio.Start();
        }
        catch (Exception e)
        {
            Core.Log.Error("Recording start failed", e);
            _audio = null;
            CancelSelectionCopy();
            Sounds.Error();
            Notify("Aufnahme nicht möglich",
                e is MicrophoneUnavailableException ? e.Message : Core.UserCopy.TechnicalFallback("Die Aufnahme konnte nicht starten.", e.Message),
                ToolTipIcon.Error);
            return;
        }
        Sounds.Start();
        SetState(State.Recording);
        // One turn only: a Dictate run leaves the image for the next Dictate Prompt or template run.
        _runShot = mode != Mode.Dictate && Core.ScreenshotPolicy.Allowed(_settings) ? _heldShot.Take() : null;
        if (_runShot != null) Core.Log.Info($"{mode}: screenshot attached ({_runShot.Data.Length / 1024} KB)");
        _overlay.ShowRecording(mode, Hotkey.Display(stored), limit,
            mic != null ? () => mic.Level : null, mic != null ? () => mic.PeakSoFar : null,
            mode == Mode.Prompt ? _settings.QuickActions : null,
            templateName: mode == Mode.Template ? _template!.DisplayName : null, screenshot: _runShot != null);
        if (_selection != null)
        {
            var pending = _selection;
            _ = pending.ContinueWith(t =>
            {
                if (_selection == pending && t.IsCompletedSuccessfully) _overlay.SetSelection(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private Task StopAndProcessAsync() => ProcessAsync(_audio!, null);

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
        _mode = run.Mode;
        _template = run.Template;
        _stopKey = run.StopKey;
        _quickAction = run.QuickAction;
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
        FailedRun? Failed() => wav == null ? null : new FailedRun(wav, _mode, seconds, selection, _quickAction, _pasteTarget, _template, _stopKey, Screenshot: _runShot);

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
                return; // already reported
            }
            _lastFailed = null; // this run worked; the old failure is history
            Core.RecordingCheckpoint.DiscardAll();
            _recent.Add(text);

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
                : await _paster.PasteAsync(text, _pasteTarget, _settings.RestoreClipboard, ct);
            Core.Log.Info($"{_mode}: {outcome}, {text.Length} chars, autoPaste {(_settings.AutoPaste ? "on" : "off")}, restore {(_settings.RestoreClipboard ? "on" : "off")}");
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
                        "Die Zwischenablage hat sich kurz vor dem Einfügen geändert. Den Text gibt es im Menü am Symbol unter „Letzte Texte“.",
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
            Notify("Offline-Modelle fehlen", e.Message + " Hier klicken, um es in den Einstellungen zu laden.", ToolTipIcon.Warning, ShowSettings);
        }
        catch (Core.LocalLlmException e)
        {
            Core.Log.Error($"{_mode}: local LLM failed", e);
            Sounds.Error();
            NotifyRetryable("Offline-Sprachmodell", e.Message, Failed());
        }
        catch (Core.GeminiException e)
        {
            Core.Log.Error($"{_mode}: Gemini {e.Kind}");
            Sounds.Error();
            if (e.Kind is Core.GeminiErrorKind.NoKey or Core.GeminiErrorKind.InvalidKey)
                Notify($"{e.Provider}-Fehler", e.Message, ToolTipIcon.Error, ShowSettings);
            else if (e.Kind is Core.GeminiErrorKind.Network or Core.GeminiErrorKind.Timeout or Core.GeminiErrorKind.Quota or Core.GeminiErrorKind.Server)
                NotifyRetryable($"{e.Provider}-Fehler", e.Message, Failed());
            else
                Notify($"{e.Provider}-Fehler", e.Message, ToolTipIcon.Error);
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
            _selectionCts?.Dispose();
            _selectionCts = null;
            _selection = null;
            _quickAction = null;
            _runShot = null; // a failed run keeps it in _lastFailed for the retry
            if (!done) _overlay.HideOverlay();
            SetState(State.Idle);
        }
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
        Notify(title, message + RetryHint, ToolTipIcon.Error, () => RetryLast());
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
        if (text.Length == 0) Notify("Nichts erkannt", "In der Aufnahme wurde keine Sprache erkannt.", ToolTipIcon.Info);
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

        string systemPrompt = Core.DictatePromptText.BuildSystemPrompt(_settings.PromptModeSystemPrompt, _settings.DictatePromptTemplate) ?? "";
        if (_quickAction != null) return await QuickActionAsync(_quickAction, wav, seconds, selection, systemPrompt, ct);

        if (_fakeGemini)
        {
            return selection == null
                ? "Angebot (Testmodus ohne Gemini)"
                : $"Bearbeitet (Testmodus ohne Gemini): {selection.Length} Zeichen markiert";
        }

        // Only Gemini hears the instruction itself; a local or OpenAI text model gets it transcribed.
            if (_settings.EffectiveTextEngine != Core.Engine.Gemini) return await PromptFromTranscriptAsync(wav, selection, systemPrompt, ct);
        if (!HasSpeech(wav))
        {
            Notify("Nichts erkannt", "In der Aufnahme wurde keine Anweisung erkannt. Bitte noch einmal sprechen.", ToolTipIcon.Info);
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
                Notify("Bitte erst Text markieren", "Zum Bearbeiten zuerst den Text markieren, dann das Kürzel drücken und die Anweisung sprechen.",
                    ToolTipIcon.Info);
                return "";
            }
        }

        // With a selection Gemini hears the instruction itself; there is no transcript to compare with.
        string text = Reply(await answer, instruction == null ? null : new[] { instruction, systemPrompt });
        Core.Log.Info($"Prompt: answer {text.Length} chars");
        RememberPrompt(selection, instruction ?? Core.PromptConversationMemory.SpokenPlaceholder, text);
        if (text.Length == 0) Notify("Nichts erkannt", "Gemini hat keinen Text geliefert. Bitte die Anweisung noch einmal sprechen.", ToolTipIcon.Info);
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
            // Parakeet gets the glossary as hotwords already; the backstops are for the cloud models.
            : _settings.EffectiveSpeechEngine == Core.Engine.Local ? await TranscribeAsync(wav, ct)
            : GlossaryBackstops(await TranscribeAsync(wav, ct));
        transcript = transcript.Trim();
        if (Core.TextCleanup.LooksLikeRefusal(transcript)) transcript = "";
        Core.Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars");
        if (transcript.Length == 0 && !Core.TemplatePrompt.AcceptsEmptyTranscript(selection))
        {
            Notify("Nichts erkannt", "In der Aufnahme wurde keine Sprache erkannt.", ToolTipIcon.Info);
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
        string text = Reply(raw, new[] { transcript, selection, template.Instructions });
        Core.Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars, answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        if (text.Length == 0) Notify("Nichts erkannt", "Es kam kein Text zurück. Bitte noch einmal versuchen.", ToolTipIcon.Info);
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
            Notify("Nichts erkannt", "In der Aufnahme wurde keine Anweisung erkannt. Bitte noch einmal sprechen.", ToolTipIcon.Info);
            return "";
        }
        if (selection == null && Core.BareEditCommand.Matches(instruction))
        {
            Core.Log.Info("Prompt: bare edit command without selection, refused");
            Sounds.Error();
            Notify("Bitte erst Text markieren", "Zum Bearbeiten zuerst den Text markieren, dann das Kürzel drücken und die Anweisung sprechen.",
                ToolTipIcon.Info);
            return "";
        }

        SetBusy(Core.UserCopy.EditingText);
        var sw = Stopwatch.StartNew();
        string text = Reply(await PromptTextAsync(selection, instruction, systemPrompt, ct), new[] { instruction, selection, systemPrompt });
        Core.Log.Info($"Prompt via transcript: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        RememberPrompt(selection, instruction, text);
        if (text.Length == 0) Notify("Nichts erkannt", "Das Sprachmodell hat keinen Text geliefert. Bitte die Anweisung noch einmal sprechen.", ToolTipIcon.Info);
        return text;
    }

    /// <summary>
    /// A quick instruction (Ctrl+1–5 or a click while recording). Whatever was said is transcribed and
    /// goes along as text, so this path always has a transcript to check the reply against.
    /// </summary>
    private async Task<string> QuickActionAsync(string quick, byte[] wav, double seconds, string? selection, string systemPrompt, CancellationToken ct)
    {
        // Under a second nothing was said yet; a recognizer would only guess at the silence.
        if (seconds >= 1.0) SetBusy(Core.UserCopy.Transcribing);
        string transcript = seconds < 1.0 ? ""
            : _fakeGemini ? "Patient klagt über Kopfschmerzen seit drei Tagen"
            // Parakeet gets the glossary as hotwords already; the backstops are for the cloud models.
            : _settings.EffectiveSpeechEngine == Core.Engine.Local ? await TranscribeAsync(wav, ct)
            : GlossaryBackstops(await TranscribeAsync(wav, ct));
        var request = Core.QuickActionRequest.Build(quick, selection, transcript);
        Core.Log.Info($"Prompt quick action: transcript {transcript.Trim().Length} chars, selection {selection?.Length ?? 0} chars");
        if (request == null)
        {
            Sounds.Error();
            Notify("Nichts zum Anwenden", "Erst Text markieren oder etwas diktieren, dann Strg+1 bis Strg+5 drücken.", ToolTipIcon.Info);
            return "";
        }
        var (target, instruction) = request.Value;
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
            raw = await PromptTextAsync(target, instruction, systemPrompt, ct);
        }
        string text = Reply(raw, new[] { transcript, selection, quick, systemPrompt });
        Core.Log.Info($"Prompt quick action: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        RememberPrompt(target, instruction, text);
        if (text.Length == 0) Notify("Nichts erkannt", "Es kam kein Text zurück. Bitte noch einmal versuchen.", ToolTipIcon.Info);
        return text;
    }

    /// <summary>
    /// Cleans a language-model reply for a plain text field and, when there is a transcript to compare
    /// with, collects the words nobody dictated (<see cref="_unsupported"/>, shown after the paste).
    /// The template is a source too, so its own wording (greeting, company name) is not flagged.
    /// </summary>
    private string Reply(string raw, string?[]? sources)
    {
        string text = Core.TextCleanup.StripMarkdown(Core.TextCleanup.Normalize(raw));
        if (sources == null || text.Length == 0) return text;
        _unsupported = Core.DictationFidelityCheck.UnsupportedWords(text, sources);
        // Count only: the words are patient data and stay out of the log.
        if (_unsupported.Count > 0) Core.Log.Info($"Prompt: {_unsupported.Count} word(s) in the reply are not in the dictation, warning shown");
        return text;
    }

    /// <summary>Speech recognition with the chosen engine: Parakeet on this PC, or Gemini / OpenAI.</summary>
    private Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, Core.PunctuationMode punctuation = Core.PunctuationMode.Automatic) =>
        _settings.EffectiveSpeechEngine switch
        {
            Core.Engine.Local => TranscribeOfflineAsync(wav, ct, punctuation),
            Core.Engine.OpenAI => TranscribeOnlineAsync(_openAi, wav, ct),
            _ => TranscribeOnlineAsync(_gemini, wav, ct),
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
    private Task<string> PromptTextAsync(string? selection, string instruction, string systemPrompt, CancellationToken ct)
    {
        switch (_settings.EffectiveTextEngine)
        {
            case Core.Engine.Local:
                if (!_offline.LlmReady)
                    SetBusy(Core.UserCopy.LoadingLlm);
                return _offline.PromptAsync(selection, instruction, systemPrompt, ct);
            case Core.Engine.OpenAI:
                return _openAi.PromptTextAsync(selection, instruction, systemPrompt, ct, HistoryFor(selection), _runShot);
            default:
                return _gemini.PromptTextAsync(selection, instruction, systemPrompt, ct, HistoryFor(selection), _runShot);
        }
    }

    /// <summary>
    /// Gemini or OpenAI. Gemini is cut above 2.5 min, OpenAI above 11 min (25 MB upload limit).
    /// Pieces are cut in pauses, four at a time: one request would hit Gemini's inline size limit
    /// and take as long as all of it. A piece that comes back as nothing but glossary words (the
    /// model filled a quiet stretch with vocabulary) is asked again once without the glossary, like the Mac.
    /// </summary>
    private Task<string> TranscribeOnlineAsync(Core.ISpeechProvider speech, byte[] wav, CancellationToken ct)
    {
        var glossary = _settings.Glossary.ToList();
        var echoTerms = Core.GlossaryPrompt.EchoTerms(glossary);
        return Core.ChunkedTranscription.TranscribeAsync(
            speech is Core.OpenAiClient ? Core.WavChunker.SplitForOpenAi(wav) : Core.WavChunker.Split(wav),
            async (chunk, c) =>
            {
                // Nothing but room noise: Gemini would answer with the glossary. Also saves the request.
                if (!HasSpeech(chunk)) return "";
                var text = await speech.TranscribeAsync(chunk, glossary, c);
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
    private string GlossaryBackstops(string raw)
    {
        var glossary = _settings.Glossary.ToList();
        var stripped = Core.TextCleanup.StripTrailingGlossaryEcho(raw, Core.GlossaryPrompt.EchoTerms(glossary));
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
        var joined = await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
            (chunk, c) => _offline.TranscribeAsync(chunk, c), maxParallel: 1, ct);
        var source = punctuation == Core.PunctuationMode.SpokenOnly
            ? Core.SpokenPunctuation.StripModelPunctuation(joined)
            : joined;
        return Core.SpokenPunctuation.Apply(source, punctuation);
    }

    // ---------------- live meeting ----------------

    /// <summary>
    /// Why a meeting cannot start (or its backlog cannot be retried) with the current engines, or null.
    /// Offline works: Parakeet transcribes meeting chunks like long dictations.
    /// </summary>
    private string? MeetingUnavailable()
    {
        if (_fakeGemini) return null;
        if (_settings.EffectiveSpeechEngine == Core.Engine.Local && !OfflineEngine.SpeechInstalled)
            return "Die Offline-Spracherkennung ist noch nicht geladen. In den Einstellungen unter „Offline-Modelle“ laden.";
        if (MissingKeyFor(Mode.Dictate) is { } provider)
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
        string raw = GlossaryBackstops(await TranscribeAsync(wav, ct));
        string text = Core.TextCleanup.DiscardImplausible(Core.TextCleanup.ForDictation(raw), Core.Wav.DurationSeconds(wav));
        return Core.TextCleanup.LooksLikeRefusal(text) ? "" : text;
    }

    /// <summary>End-of-meeting summary with the Dictate Prompt model of the chosen text engine.</summary>
    private Task<string> SummarizeMeetingAsync(string transcript, CancellationToken ct)
    {
        if (_fakeGemini) return Task.FromResult("Hauptpunkte:\n- Testmeeting ohne Gemini");
        var text = Core.MeetingPrompts.Clip(transcript);
        return _settings.EffectiveTextEngine switch
        {
            Core.Engine.Local when !OfflineEngine.LlmInstalled(_offline.SelectedLlm) =>
                throw new InvalidOperationException("The offline language model is not installed."),
            Core.Engine.Local => _offline.PromptAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
            Core.Engine.OpenAI => _openAi.PromptTextAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
            _ => _gemini.PromptTextAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
        };
    }

    // ---------------- glossary ----------------

    /// <summary>
    /// Copies the selection and appends it to the Glossary as is: the way to teach the spelling of a
    /// name from a document or the practice software, with no model in between (works offline too).
    /// </summary>
    private async Task AddSelectionToGlossaryAsync()
    {
        if (_state != State.Idle || _glossaryBusy || _selection != null)
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
    /// Esc and Ctrl+1–5 belong to the other programs except while we need them: Esc while recording or processing,
    /// Ctrl+1–5 only while a Dictate Prompt recording runs.
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
            State.Recording => (Core.UserCopy.RecordingStatus(key), TrayIcons.Recording),
            State.Processing => (_busyStatus, TrayIcons.Processing),
            _ when MissingKey => ("Kein API-Schlüssel eingetragen", TrayIcons.Ready),
            _ when !OfflineModelsReady(Mode.Dictate) => ("Spracherkennung noch nicht geladen", TrayIcons.Ready),
            _ when _settings.OfflineMode => ("Bereit (offline, nichts verlässt diesen PC)", TrayIcons.Ready),
            _ => ("Bereit", TrayIcons.Ready),
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

    private void ShowUpdateNotice()
    {
        var update = _updates.Available;
        if (update == null) return;
        Notify($"Version {update.Version} ist da",
            "Hier klicken, um zu sehen, was neu ist, und es zu installieren. Ohne Klick bleibt alles, wie es ist.",
            ToolTipIcon.Info, _updates.ShowDialog);
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
        bool settingsInFront = f is { IsDisposed: false, Visible: true } && ClipboardPaster.ForegroundIsOwnWindow();
        bool wanted = Core.HotkeyPolicy.WantGlobalHotkeys(settingsInFront, _state == State.Idle);
        if (wanted == _globalHotkeysOn) return;
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
            SetStatusText(status);
            _overlay.ShowProcessing(status);
        }
    }

    private void SetStatusText(string status)
    {
        string tip = $"WhisperShortcut – {status}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127]; // NotifyIcon throws above 127 characters; also the tray's screen-reader name
        _statusItem.Text = status;
    }

    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        Core.Log.Info($"Notify: {title}");  // title only: the text can quote an error message
        _toast.Present(title, text, icon, onClick);
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
    /// First start (and <c>--welcome</c> for VM screenshots): the settings window with the "what to do
    /// now" box, so nobody has to find the tray icon first.
    /// </summary>
    public void OpenWelcome() => _ui.Post(_ => ShowSettings(welcome: true), null);

    private void ShowSettings() => ShowSettings(welcome: false);

    private void ShowSettings(bool welcome)
    {
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
        var form = _settingsForm = new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, welcome, _mcp);
        // Activation messages can still arrive after FormClosed: only the open window may re-evaluate.
        _settingsForm.Activated += (_, _) => { if (_settingsForm == form) SyncGlobalHotkeys(); };
        // Posted: while Deactivate runs the new foreground window is not set yet.
        _settingsForm.Deactivate += (_, _) => { if (_settingsForm == form) _ui.Post(_ => SyncGlobalHotkeys(), null); };
        // Save keeps the window open: apply right away, check the hotkeys once the window leaves the front.
        _settingsForm.Saved += () =>
        {
            Sounds.Enabled = _settings.Sounds;
            _hotkeyCheckPending = true;
            if (_settings.OfflineMode) _promptMemory.Clear();
            SetState(_state); // key may have been added: tooltip "Bereit" instead of "Kein API-Schlüssel"
            _offline.Prewarm(); // offline mode may just have been switched on
            _offline.GlossaryChanged(); // the Glossary may have been edited
            DropScreenshotIfNotAllowed();
            _mcp.SyncOfflineMode();
        };
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
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
    /// Tray switch between online and offline, e.g. before a session with patient data. Without the
    /// models (or without a key for online) it opens the settings instead of leaving a mode that cannot work.
    /// </summary>
    private void ToggleOffline()
    {
        if (_state != State.Idle) return;
        bool offline = !_settings.OfflineMode;
        if (offline && !OfflineEngine.SpeechInstalled)
        {
            Notify("Offline-Modelle fehlen", "Für den Offline-Modus zuerst die Spracherkennung laden: hier klicken.", ToolTipIcon.Info, ShowSettings);
            return;
        }
        _settings.OfflineMode = offline;
        if (offline) _promptMemory.Clear();
        DropScreenshotIfNotAllowed();
        // Back to the chosen engines: they may need a key that was never entered.
        if (!offline && MissingKeyFor(Mode.Prompt) is { } provider)
        {
            _settings.OfflineMode = true;
            Notify("Schlüssel fehlt", $"Für den Online-Modus zuerst den {provider}-Schlüssel eintragen: hier klicken.", ToolTipIcon.Info, ShowSettings);
            return;
        }
        TrySave();
        Core.Log.Info($"Tray: offline mode {(offline ? "on" : "off")}");
        _mcp.SyncOfflineMode();
        _offline.Prewarm();
        SetState(_state);
        _overlay.ShowDone(offline ? "Offline: nichts verlässt den PC" : EnginesLabel(), neutral: true);
    }

    /// <summary>"Sprache: Gemini · Text: OpenAI", or one name when both are the same.</summary>
    private string EnginesLabel()
    {
        static string Name(Core.Engine e) => e switch { Core.Engine.Local => "auf diesem PC", Core.Engine.OpenAI => "OpenAI", _ => "Gemini" };
        var (speech, text) = (_settings.EffectiveSpeechEngine, _settings.EffectiveTextEngine);
        if (speech == Core.Engine.Local && text == Core.Engine.Local) return "Alles auf diesem PC";
        return speech == text ? $"Online mit {Name(speech)}" : $"Sprache: {Name(speech)} · Text: {Name(text)}";
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
        _snipCts?.Cancel();
        _shotExpiry.Dispose();
        _updateNoticeTimer.Dispose();
        _updates.Dispose();
        _overlay.Dispose();
        _toast.Dispose();
        _meeting.Dispose();
        _hotkeys.Dispose();
        _offline.Dispose();
        _mcp.Dispose();
        ExitThread();
    }
}
