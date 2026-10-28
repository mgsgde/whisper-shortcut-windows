using System.Diagnostics;

namespace WhisperShortcut;

internal enum Mode { Dictate, Prompt }

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
    /// In memory only, never written to disk: it is patient data. Dropped after the next success.
    /// </summary>
    private sealed record FailedRun(byte[] Wav, Mode Mode, double Seconds, string? Selection, string? QuickAction, IntPtr Target);

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
    private Action? _balloonClick;
    private readonly UpdateService _updates;
    private readonly ToolStripMenuItem _updateItem;
    // A found update waits for the end of a recording; the notice then comes a little later, so it does not
    // replace a "Bitte prüfen" balloon that was just shown.
    private readonly System.Windows.Forms.Timer _updateNoticeTimer = new() { Interval = 20_000 };
    private bool _updateNoticePending;
    private bool _glossaryBusy;
    private bool _globalHotkeysOn;
    private readonly Core.HotkeyPressGate _pressGate = new();
    private readonly System.Windows.Forms.Timer _keyUpTimer = new() { Interval = 30 };
    private Keys _armedKey;
    private long _armedKeyDeadline;
    private readonly ToolStripMenuItem _offlineItem;
    private readonly SynchronizationContext _ui;

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
            _settings.PromptSystemPrompt = QuoteTemplate.Default;
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
        _copyLast = new ToolStripMenuItem("Letzten Text kopieren", null, (_, _) => CopyRecent(_recent.Latest));
        _recentMenu = new ToolStripMenuItem("Letzte Texte");
        _retryItem = new ToolStripMenuItem("Letzte Aufnahme erneut verarbeiten", null, (_, _) => RetryLast()) { Enabled = false };
        _updates = new UpdateService(_settings, () => _state == State.Idle, Quit);
        _updateItem = new ToolStripMenuItem("Update installieren …", null, (_, _) => _updates.ShowDialog())
        {
            Visible = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        var title = new ToolStripMenuItem("WhisperShortcut") { Enabled = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _offlineItem = new ToolStripMenuItem("Offline: nichts verlässt den PC", null, (_, _) => ToggleOffline()) { CheckOnClick = false };
        var settingsItem = new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        var logItem = new ToolStripMenuItem("Protokoll öffnen", null, (_, _) => OpenLog());
        var quitItem = new ToolStripMenuItem("Beenden", null, (_, _) => Quit());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            title, _statusItem, _updateItem, new ToolStripSeparator(), _dictateInfo, _promptInfo, _glossaryInfo, new ToolStripSeparator(),
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

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = new NotifyIcon { Icon = TrayIcons.Ready, ContextMenuStrip = menu, Visible = true };
        // One left click opens the settings (Click alone would also fire on right click, which opens the menu).
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();

        _keyUpTimer.Tick += (_, _) => PollArmedKey();
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

        if (firstRun && !testRun)
        {
            OpenWelcome();
        }
        else if (MissingKey)
        {
            Notify("Willkommen bei WhisperShortcut",
                "Bitte zuerst den API-Schlüssel eintragen: hier klicken oder auf das blaue Symbol unten rechts.",
                ToolTipIcon.Info, ShowSettings);
        }
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
        foreach (var (stored, action) in new (string, Action)[]
        {
            (_settings.DictateHotkey, () => OnHotkey(Mode.Dictate)),
            (_settings.PromptHotkey, () => OnHotkey(Mode.Prompt)),
            (_settings.GlossaryHotkey, () => _ = AddSelectionToGlossaryAsync()),
        })
        {
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, action))
                failed.Add(Hotkey.Display(stored));
        }
        SyncSessionHotkeys();
        _dictateInfo.Text = $"Diktieren: {Hotkey.Display(_settings.DictateHotkey)}";
        _promptInfo.Text = $"Text bearbeiten oder schreiben lassen: {Hotkey.Display(_settings.PromptHotkey)}";
        _glossaryInfo.Text = $"Markierung ins Glossar: {Hotkey.Display(_settings.GlossaryHotkey)}";
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
        Core.Log.Info($"Hotkey {mode} in state {_state}");
        var now = Environment.TickCount64;
        if (_state != State.Idle && !_pressGate.Accept(now))
        {
            Core.Log.Info($"Hotkey {mode} in state {_state} ignored: {(_pressGate.Released ? "within 300 ms" : "key still held")}");
            return;
        }
        var before = _state;
        switch (_state)
        {
            case State.Idle:
                StartRecording(mode);
                break;
            case State.Recording:
                _ = StopAndProcessAsync();
                break;
            case State.Processing:
                _cts?.Cancel();
                break;
        }
        if (_state != before && Hotkey.TryParse(mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey, out var key))
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
        Core.Log.Info($"Esc in state {_state}");
        if (_state == State.Processing)
        {
            _cts?.Cancel(); // same path as a hotkey press during processing
            return;
        }
        if (_state != State.Recording) return;
        (_audio as MicAudioSource)?.Cancel();
        _audio = null;
        CancelSelectionCopy(); // no Ctrl+C into the user's window after the cancel
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

    // ---------------- recording ----------------

    /// <summary>
    /// The engines a mode uses: Dictate only speech recognition; Dictate Prompt the text engine and
    /// speech recognition too (the instruction is transcribed, or checked for a bare "korrigiere").
    /// </summary>
    private IEnumerable<Core.Engine> EnginesFor(Mode mode) => mode == Mode.Dictate
        ? new[] { _settings.EffectiveSpeechEngine }
        : new[] { _settings.EffectiveSpeechEngine, _settings.EffectiveTextEngine };

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

    private void StartRecording(Mode mode)
    {
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
        if (mode == Mode.Prompt && _settings.EffectiveTextEngine == Core.Engine.Local && OfflineEngine.LlmUnsupportedCpu)
        {
            Core.Log.Info("Hotkey Prompt with the local text engine, but this CPU cannot run the language model");
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
        _quickAction = null;
        // Mac pastes only into the app that was in front now; the hotkey was pressed in the user's window.
        _pasteTarget = ClipboardPaster.ForegroundRoot();
        // Copy the selection right away, while the user's selection is still there. It runs while recording.
        _selectionCts = mode == Mode.Prompt ? new CancellationTokenSource() : null;
        _selection = _selectionCts != null ? _paster.CopySelectionAsync(_selectionCts.Token) : null;
        // Loading the language model takes seconds to half a minute; do it while the user speaks.
        if (mode == Mode.Prompt && _settings.EffectiveTextEngine == Core.Engine.Local) _offline.PrewarmLlm();

        var limit = mode == Mode.Dictate ? MicAudioSource.DictateLimit : MicAudioSource.PromptLimit;
        MicAudioSource? mic = null;
        if (_testWav != null)
        {
            _audio = new Core.FileAudioSource(_testWav);
        }
        else
        {
            mic = new MicAudioSource(limit, _settings.Microphone);
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
            Notify("Aufnahme nicht möglich", e.Message, ToolTipIcon.Error);
            return;
        }
        Sounds.Start();
        SetState(State.Recording);
        _overlay.ShowRecording(mode, Hotkey.Display(mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey),
            limit, mic != null ? () => mic.Level : null, mic != null ? () => mic.PeakSoFar : null, _settings.QuickActions);
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
    /// Tray menu or balloon click: run the last failed recording again. Like the Mac it pastes into the
    /// window the recording was made for; after the click the taskbar is in front, never the user's field.
    /// </summary>
    private void RetryLast()
    {
        if (_state != State.Idle || _lastFailed == null) return;
        var run = _lastFailed;
        Core.Log.Info($"{run.Mode}: retrying the last recording ({run.Seconds:0.0} s)");
        _mode = run.Mode;
        _quickAction = run.QuickAction;
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
        SetState(State.Processing);
        _overlay.ShowProcessing(_quickAction != null ? $"{_quickAction} …" : "Wird verarbeitet …");
        Sounds.Stop();
        _unsupported = Array.Empty<string>();
        bool done = false;
        byte[]? wav = null;
        double seconds = 0;
        string? selection = null;
        // What a retry needs; null when the audio itself is lost (then there is nothing to try again).
        FailedRun? Failed() => wav == null ? null : new FailedRun(wav, _mode, seconds, selection, _quickAction, _pasteTarget);

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
                catch (Core.AudioSilentException) when (_quickAction != null)
                {
                    // Text marked, Ctrl+1 pressed without a word: the instruction applies to the selection alone.
                    wav = Array.Empty<byte>();
                }
                seconds = wav.Length == 0 ? 0 : Core.Wav.DurationSeconds(wav);
                Core.Log.Info($"{_mode}: recorded {seconds:0.0} s");
                // A quick instruction needs no speech: mark text, press the hotkey and right away Ctrl+1.
                if (seconds < 0.5 && _quickAction == null)
                {
                    // A double press, not a recording: say so instead of a misleading microphone error.
                    Notify("Aufnahme zu kurz", "Kürzel drücken, sprechen, dann erneut drücken.", ToolTipIcon.Info);
                    return;
                }
                if (_mode == Mode.Prompt && _selection != null) selection = await _selection;
            }

            string text = _mode == Mode.Dictate
                ? await DictateAsync(wav, seconds, ct)
                : await PromptAsync(wav, seconds, selection, retry != null, ct);
            ct.ThrowIfCancellationRequested();

            if (text.Length == 0) return; // already reported
            _lastFailed = null; // this run worked; the old failure is history
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

            var outcome = _settings.AutoPaste
                ? await _paster.PasteAsync(text, _pasteTarget, _settings.RestoreClipboard, ct)
                : (_paster.KeepOnClipboard(text) ? PasteOutcome.ClipboardOnlyByChoice : PasteOutcome.Failed);
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
                    or PasteOutcome.ClipboardOnlyOwnWindow when _unsupported.Count > 0:
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
                Notify("Gemini-Fehler", e.Message, ToolTipIcon.Error, ShowSettings);
            else if (e.Kind is Core.GeminiErrorKind.Network or Core.GeminiErrorKind.Timeout or Core.GeminiErrorKind.Quota or Core.GeminiErrorKind.Server)
                NotifyRetryable("Gemini-Fehler", e.Message, Failed());
            else
                Notify("Gemini-Fehler", e.Message, ToolTipIcon.Error);
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
        string raw = _fakeGemini
            ? "das ist ein test diktat aus der virtuellen maschine"
            : await TranscribeAsync(wav, ct);
        raw = GlossaryBackstops(raw);
        string text = Core.TextCleanup.DiscardImplausible(Core.TextCleanup.ForDictation(raw), seconds);
        // A chatbot answer ("please provide the audio") or our own instruction is no dictation.
        if (Core.TextCleanup.LooksLikeRefusal(text)) text = "";
        Core.Log.Info($"Dictate: {raw.Length} chars raw, {text.Length} chars after cleanup");
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

        string systemPrompt = Core.DictatePromptText.BuildSystemPrompt(_settings.PromptModeSystemPrompt, _settings.PromptSystemPrompt) ?? "";
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
        var answer = _gemini.PromptAsync(wav, selection, systemPrompt, ct);

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
        if (text.Length == 0) Notify("Nichts erkannt", "Gemini hat keinen Text geliefert. Bitte die Anweisung noch einmal sprechen.", ToolTipIcon.Info);
        return text;
    }

    /// <summary>
    /// Dictate Prompt with a text model that cannot hear (local, as on the Mac, or OpenAI): the
    /// instruction is transcribed with the chosen speech engine and sent as text with the selection.
    /// </summary>
    private async Task<string> PromptFromTranscriptAsync(byte[] wav, string? selection, string systemPrompt, CancellationToken ct)
    {
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

        var sw = Stopwatch.StartNew();
        string text = Reply(await PromptTextAsync(selection, instruction, systemPrompt, ct), new[] { instruction, selection, systemPrompt });
        Core.Log.Info($"Prompt via transcript: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
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
    private Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) =>
        _settings.EffectiveSpeechEngine switch
        {
            Core.Engine.Local => TranscribeOfflineAsync(wav, ct),
            Core.Engine.OpenAI => TranscribeOnlineAsync(_openAi, wav, ct),
            _ => TranscribeOnlineAsync(_gemini, wav, ct),
        };

    /// <summary>A text-only Dictate Prompt call with the chosen text engine.</summary>
    private Task<string> PromptTextAsync(string? selection, string instruction, string systemPrompt, CancellationToken ct)
    {
        switch (_settings.EffectiveTextEngine)
        {
            case Core.Engine.Local:
                if (!_offline.LlmReady)
                {
                    SetStatusText("Sprachmodell wird geladen …");
                    _overlay.ShowProcessing("Sprachmodell wird geladen …");
                }
                return _offline.PromptAsync(selection, instruction, systemPrompt, ct);
            case Core.Engine.OpenAI:
                return _openAi.PromptTextAsync(selection, instruction, systemPrompt, ct);
            default:
                return _gemini.PromptTextAsync(selection, instruction, systemPrompt, ct);
        }
    }

    /// <summary>
    /// Gemini or OpenAI. Above 2.5 min the recording goes out in ~2-minute pieces, cut in pauses, four at a time:
    /// one request would hit Gemini's inline size limit and take as long as all of it. A piece that comes
    /// back as nothing but glossary words (the model filled a quiet stretch with vocabulary) is asked
    /// again once without the glossary, like the Mac.
    /// </summary>
    private Task<string> TranscribeOnlineAsync(Core.ISpeechProvider speech, byte[] wav, CancellationToken ct)
    {
        var glossary = _settings.Glossary.ToList();
        var echoTerms = Core.GlossaryPrompt.EchoTerms(glossary);
        return Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.Split(wav), async (chunk, c) =>
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
    private async Task<string> TranscribeOfflineAsync(byte[] wav, CancellationToken ct) =>
        Core.SpokenPunctuation.Apply(await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
            (chunk, c) => _offline.TranscribeAsync(chunk, c), maxParallel: 1, ct));

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
        string key = Hotkey.Display(_mode == Mode.Dictate ? _settings.DictateHotkey : _settings.PromptHotkey);
        (string status, Icon icon) = state switch
        {
            State.Recording => ($"Aufnahme läuft – {key} beendet sie", TrayIcons.Recording),
            State.Processing => ("Wird verarbeitet …", TrayIcons.Processing),
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

    private void SetStatusText(string status)
    {
        string tip = $"WhisperShortcut – {status}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127]; // NotifyIcon throws above 127 characters
        _statusItem.Text = status;
    }

    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        Core.Log.Info($"Notify: {title}");  // title only: the text can quote an error message
        _icon.ShowBalloonTip(6000, title, text, icon);
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
        var form = _settingsForm = new SettingsForm(_settings, _offline, _gemini, _openAi, _updates, welcome);
        // Activation messages can still arrive after FormClosed: only the open window may re-evaluate.
        _settingsForm.Activated += (_, _) => { if (_settingsForm == form) SyncGlobalHotkeys(); };
        // Posted: while Deactivate runs the new foreground window is not set yet.
        _settingsForm.Deactivate += (_, _) => { if (_settingsForm == form) _ui.Post(_ => SyncGlobalHotkeys(), null); };
        // Save keeps the window open: apply right away, check the hotkeys once the window leaves the front.
        _settingsForm.Saved += () =>
        {
            Sounds.Enabled = _settings.Sounds;
            _hotkeyCheckPending = true;
            SetState(_state); // key may have been added: tooltip "Bereit" instead of "Kein API-Schlüssel"
            _offline.Prewarm(); // offline mode may just have been switched on
            _offline.GlossaryChanged(); // the Glossary may have been edited
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
        // Back to the chosen engines: they may need a key that was never entered.
        if (!offline && MissingKeyFor(Mode.Prompt) is { } provider)
        {
            _settings.OfflineMode = true;
            Notify("Schlüssel fehlt", $"Für den Online-Modus zuerst den {provider}-Schlüssel eintragen: hier klicken.", ToolTipIcon.Info, ShowSettings);
            return;
        }
        TrySave();
        Core.Log.Info($"Tray: offline mode {(offline ? "on" : "off")}");
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

    private void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception e) { Core.Log.Error("Saving settings failed", e); }
    }

    private static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Core.Log.DefaultPath) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error("Opening log failed", e); }
    }

    private void Quit()
    {
        _cts?.Cancel();
        _selectionCts?.Cancel();
        (_audio as MicAudioSource)?.Cancel();
        _icon.Visible = false;
        _icon.Dispose();
        _keyUpTimer.Dispose();
        _updateNoticeTimer.Dispose();
        _updates.Dispose();
        _overlay.Dispose();
        _hotkeys.Dispose();
        _offline.Dispose();
        ExitThread();
    }
}
