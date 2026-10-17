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

    private readonly string? _testWav;
    private readonly bool _fakeGemini;
    private readonly NotifyIcon _icon;
    private readonly HotkeyService _hotkeys;
    private readonly ClipboardPaster _paster;
    private readonly Core.Settings _settings;
    private readonly Core.GeminiClient _gemini;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _dictateInfo;
    private readonly ToolStripMenuItem _promptInfo;

    private State _state = State.Idle;
    private Mode _mode;
    private Core.IAudioSource? _audio;
    private Task<string?>? _selection;
    private CancellationTokenSource? _cts;
    private SettingsForm? _settingsForm;
    private Action? _balloonClick;
    private readonly SynchronizationContext _ui;

    public TrayApp(string? testWav, bool fakeGemini)
    {
        _testWav = testWav;
        _fakeGemini = fakeGemini;

        bool firstRun = !File.Exists(Core.Settings.DefaultPath);
        _settings = Core.Settings.Load(Core.Settings.DefaultPath, new DpapiProtector());
        if (firstRun)
        {
            _settings.PromptSystemPrompt = QuoteTemplate.Default;
            TrySave();
        }
        _gemini = new Core.GeminiClient(_settings);

        _hotkeys = new HotkeyService();
        _paster = new ClipboardPaster(_hotkeys.Handle);

        _statusItem = new ToolStripMenuItem("Bereit") { Enabled = false };
        _dictateInfo = new ToolStripMenuItem { Enabled = false };
        _promptInfo = new ToolStripMenuItem { Enabled = false };
        var title = new ToolStripMenuItem("WhisperShortcut") { Enabled = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        var settingsItem = new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings());
        var logItem = new ToolStripMenuItem("Protokoll öffnen", null, (_, _) => OpenLog());
        var quitItem = new ToolStripMenuItem("Beenden", null, (_, _) => Quit());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            title, _statusItem, new ToolStripSeparator(), _dictateInfo, _promptInfo, new ToolStripSeparator(),
            settingsItem, logItem, new ToolStripSeparator(), quitItem,
        });

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = new NotifyIcon { Icon = TrayIcons.Ready, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();

        RegisterHotkeys();
        SetState(State.Idle);

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) && !_fakeGemini)
        {
            Notify("Willkommen bei WhisperShortcut",
                "Bitte zuerst den Gemini-API-Schlüssel eintragen. Hier klicken oder Rechtsklick auf das Symbol → Einstellungen.",
                ToolTipIcon.Info, ShowSettings);
        }
    }

    // ---------------- hotkeys ----------------

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        foreach (var (stored, mode) in new[] { (_settings.DictateHotkey, Mode.Dictate), (_settings.PromptHotkey, Mode.Prompt) })
        {
            if (!Hotkey.TryParse(stored, out var key) || !_hotkeys.Register(key, () => OnHotkey(mode)))
                failed.Add(Hotkey.Display(stored));
        }
        _dictateInfo.Text = $"Diktieren: {Hotkey.Display(_settings.DictateHotkey)}";
        _promptInfo.Text = $"Text bearbeiten / schreiben: {Hotkey.Display(_settings.PromptHotkey)}";
        if (failed.Count > 0)
        {
            Notify("Tastenkürzel belegt",
                $"{string.Join(" und ", failed)} wird schon von einem anderen Programm benutzt. Bitte in den Einstellungen ein anderes wählen.",
                ToolTipIcon.Warning, ShowSettings);
        }
    }

    private void OnHotkey(Mode mode)
    {
        Core.Log.Info($"Hotkey {mode} in state {_state}");
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
    }

    // ---------------- recording ----------------

    private void StartRecording(Mode mode)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) && !_fakeGemini)
        {
            Core.Log.Info("Hotkey pressed but no API key (Schlüssel fehlt)");
            Sounds.Error();
            Notify("Schlüssel fehlt", "Kein API-Schlüssel eingetragen. Hier klicken, um ihn in den Einstellungen einzutragen.",
                ToolTipIcon.Warning, ShowSettings);
            return;
        }

        _mode = mode;
        // Copy the selection right away, while the user's selection is still there. It runs while recording.
        _selection = mode == Mode.Prompt ? _paster.CopySelectionAsync() : null;

        if (_testWav != null)
        {
            _audio = new Core.FileAudioSource(_testWav);
        }
        else
        {
            var mic = new MicAudioSource();
            mic.MaxDurationReached += () => _ui.Post(_ =>
            {
                if (_state != State.Recording) return;
                Notify("Aufnahme beendet", "Nach 5 Minuten wird die Aufnahme automatisch beendet und verarbeitet.", ToolTipIcon.Info);
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
            Sounds.Error();
            Notify("Aufnahme nicht möglich", e.Message, ToolTipIcon.Error);
            return;
        }
        Sounds.Start();
        SetState(State.Recording);
    }

    private async Task StopAndProcessAsync()
    {
        var audio = _audio!;
        _audio = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetState(State.Processing);
        Sounds.Stop();

        try
        {
            byte[] wav = await audio.StopAsync();
            double seconds = Core.Wav.DurationSeconds(wav);
            Core.Log.Info($"{_mode}: recorded {seconds:0.0} s");

            string text = _mode == Mode.Dictate
                ? await DictateAsync(wav, seconds, ct)
                : await PromptAsync(wav, ct);
            ct.ThrowIfCancellationRequested();

            if (text.Length == 0) return; // already reported

            var outcome = await _paster.PasteAsync(text);
            Core.Log.Info($"{_mode}: {outcome}, {text.Length} chars");
            switch (outcome)
            {
                case PasteOutcome.ClipboardOnlyElevatedTarget:
                    Notify("Bitte selbst einfügen",
                        "Das Zielprogramm läuft als Administrator, dorthin darf WhisperShortcut nicht tippen. Der Text liegt in der Zwischenablage: Strg+V drücken.",
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
            Core.Log.Info($"{_mode}: cancelled by user");
            Notify("Abgebrochen", "Die Verarbeitung wurde abgebrochen.", ToolTipIcon.None);
        }
        catch (Core.GeminiException e)
        {
            Core.Log.Error($"{_mode}: Gemini {e.Kind}");
            Sounds.Error();
            Notify("Gemini-Fehler", e.Message, ToolTipIcon.Error,
                e.Kind is Core.GeminiErrorKind.NoKey or Core.GeminiErrorKind.InvalidKey ? ShowSettings : null);
        }
        catch (Exception e) when (e is Core.AudioSilentException or MicrophoneUnavailableException)
        {
            Core.Log.Error($"{_mode}: microphone problem {e.GetType().Name}");
            Sounds.Error();
            Notify("Mikrofon", e.Message, ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            Core.Log.Error($"{_mode}: unexpected error", e);
            Sounds.Error();
            Notify("Fehler", "Etwas ist schiefgelaufen. Details stehen im Protokoll (Rechtsklick auf das Symbol → Protokoll öffnen).", ToolTipIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _selection = null;
            SetState(State.Idle);
        }
    }

    private async Task<string> DictateAsync(byte[] wav, double seconds, CancellationToken ct)
    {
        string raw = _fakeGemini
            ? "das ist ein test diktat aus der virtuellen maschine"
            : await _gemini.TranscribeAsync(wav, _settings.Glossary, ct);
        string text = Core.TextCleanup.DiscardImplausible(Core.TextCleanup.ForDictation(raw), seconds);
        Core.Log.Info($"Dictate: {raw.Length} chars raw, {text.Length} chars after cleanup");
        if (text.Length == 0) Notify("Nichts erkannt", "In der Aufnahme wurde keine Sprache erkannt.", ToolTipIcon.Info);
        return text;
    }

    private async Task<string> PromptAsync(byte[] wav, CancellationToken ct)
    {
        string? selection = _selection != null ? await _selection : null;
        Core.Log.Info($"Prompt: {(selection == null ? "no selection (compose)" : $"selection {selection.Length} chars")}");

        if (_fakeGemini)
        {
            return selection == null
                ? "Angebot (Testmodus ohne Gemini)"
                : $"Bearbeitet (Testmodus ohne Gemini): {selection.Length} Zeichen markiert";
        }

        string systemPrompt = QuoteTemplate.BuildSystemPrompt(_settings.PromptSystemPrompt) ?? "";
        var answer = _gemini.PromptAsync(wav, selection, systemPrompt, ct);

        if (selection == null)
        {
            // Nothing selected and the instruction is only "korrigiere" / "mach das freundlicher":
            // there is nothing to edit, and the model would invent a text. Transcribe in parallel to check.
            string instruction = await _gemini.TranscribeAsync(wav, _settings.Glossary, ct);
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

        string text = Core.TextCleanup.Normalize(await answer);
        Core.Log.Info($"Prompt: answer {text.Length} chars");
        if (text.Length == 0) Notify("Nichts erkannt", "Gemini hat keinen Text geliefert. Bitte die Anweisung noch einmal sprechen.", ToolTipIcon.Info);
        return text;
    }

    // ---------------- UI ----------------

    private void SetState(State state)
    {
        _state = state;
        (string status, Icon icon) = state switch
        {
            State.Recording => (_mode == Mode.Dictate ? "Nimmt auf (Diktat) …" : "Nimmt Anweisung auf …", TrayIcons.Recording),
            State.Processing => ("Verarbeitet …", TrayIcons.Processing),
            _ => ("Bereit", TrayIcons.Ready),
        };
        _icon.Icon = icon;
        _icon.Text = $"WhisperShortcut – {status}";
        _statusItem.Text = status;
    }

    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(6000, title, text, icon);
    }

    private void ShowSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }
        if (_state != State.Idle) return;

        // While the window is open the global hotkeys are off, so the hotkey field can record Ctrl+Alt+D itself.
        _hotkeys.UnregisterAll();
        _settingsForm = new SettingsForm(_settings);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            RegisterHotkeys();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
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
        (_audio as MicAudioSource)?.Cancel();
        _icon.Visible = false;
        _icon.Dispose();
        _hotkeys.Dispose();
        ExitThread();
    }
}
