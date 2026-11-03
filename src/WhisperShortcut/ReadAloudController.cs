namespace WhisperShortcut;

/// <summary>
/// Vorlesen (Mac Read Aloud, MenuBarController triggerReadSelectedTextAloud / beginReadAloudProcessing): copy the
/// selection, optionally let the text model make it speakable (Smart Rewrite), synthesize it in chunks and play the chunks
/// as they arrive. Runs beside the tray's own state machine, like the Mac's <c>speaking</c> state: it does not count as
/// busy, so a dictation may start while it speaks. The same shortcut stops it.
/// </summary>
internal sealed class ReadAloudController : IDisposable
{
    private enum Phase { Idle, Preparing, Speaking }

    private readonly Core.Settings _settings;
    private readonly ClipboardPaster _paster;
    private readonly Core.GeminiClient _gemini;
    private readonly Core.OpenAiClient _openAi;
    private readonly Core.TtsClient _tts;
    private readonly Action<string, string, ToolTipIcon, Action?> _notify;
    private readonly Action _showSettings;
    private readonly Action _save;
    private readonly SynchronizationContext _ui;
    private readonly ReadAloudOverlay _overlay = new();

    private Phase _phase = Phase.Idle;
    private CancellationTokenSource? _cts;
    private Core.SpeechPlaybackBuffer? _buffer;
    private TtsPlayer? _player;
    // Counts runs so a late callback of a stopped run cannot touch the next one.
    private int _run;

    /// <summary>Raised on the UI thread when it starts, starts speaking or ends.</summary>
    public event Action? StateChanged;

    public ReadAloudController(Core.Settings settings, ClipboardPaster paster, Core.GeminiClient gemini, Core.OpenAiClient openAi,
        Action<string, string, ToolTipIcon, Action?> notify, Action showSettings, Action save, SynchronizationContext ui)
    {
        _settings = settings;
        _paster = paster;
        _gemini = gemini;
        _openAi = openAi;
        _tts = new Core.TtsClient(settings);
        _notify = notify;
        _showSettings = showSettings;
        _save = save;
        _ui = ui;
        _overlay.CommandClicked += OnCommand;
        _overlay.SeekClicked += share =>
        {
            if (_buffer is { } b) b.Seek(share * b.DurationSeconds);
        };
    }

    /// <summary>Preparing or speaking: the shortcut and the menu row stop it.</summary>
    public bool IsActive => _phase != Phase.Idle;

    /// <summary>Tray status line (Mac: „⏳ Preparing Read Aloud...“ / „🔊 Speaking...“).</summary>
    public string StatusText => _phase == Phase.Speaking ? "🔊 Spricht …" : "⏳ Vorlesen wird vorbereitet …";

    /// <summary>True while audio plays (or waits for its next chunk); false while still preparing.</summary>
    public bool IsSpeaking => _phase == Phase.Speaking;

    /// <summary>Hotkey or menu row: starts on the current selection, or stops what runs.</summary>
    public void Toggle()
    {
        if (IsActive)
        {
            Core.Log.Info("Read aloud: stopped by the user");
            Stop();
            return;
        }
        _ = StartAsync();
    }

    /// <summary>Stops synthesis and playback, hides the card. Safe to call when idle.</summary>
    public void Stop()
    {
        _run++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _player?.Dispose();
        _player = null;
        _buffer = null;
        _overlay.HideOverlay();
        SetPhase(Phase.Idle);
    }

    private async Task StartAsync()
    {
        // Mac availability check: no cloud voice offline (Windows has no on-device voice), and the provider needs its key.
        if (_settings.OfflineMode)
        {
            _notify("Vorlesen", "Der Offline-Modus ist an, deshalb lassen sich die Cloud-Stimmen zum Vorlesen nicht nutzen. Schalte den "
                + "Offline-Modus unter Einstellungen → Datenschutz & Berechtigungen aus, wenn der Text dafür an den Anbieter gehen darf.",
                ToolTipIcon.Info, _showSettings);
            return;
        }
        var model = _settings.ReadAloudTtsModel;
        if (MissingKeyMessage(model) is { } missing)
        {
            Sounds.Error();
            _notify("API-Schlüssel erforderlich", missing, ToolTipIcon.Warning, _showSettings);
            return;
        }

        int run = ++_run;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetPhase(Phase.Preparing);
        try
        {
            string? selection = await _paster.CopySelectionAsync(ct);
            if (run != _run) return;
            var text = selection?.Trim() ?? "";
            if (text.Length == 0)
            {
                Core.Log.Info("Read aloud: no selection");
                Stop();
                _notify("Vorlesen", "Kein Text markiert. Markiere zuerst Text und drück dann das Tastenkürzel für „Vorlesen“.",
                    ToolTipIcon.Info, null);
                return;
            }
            Core.Log.Info($"Read aloud: selection {text.Length} chars, model {model.Id}, rewrite {(_settings.ReadAloudSmartRewrite ? "on" : "off")}");
            _overlay.ShowPreparing();

            var spoken = await MaybeRewriteAsync(text, ct);
            if (run != _run) return;
            // Always last (Mac sanitizedForSpeech): even the rewrite can hand back Markdown. Nothing left = read the original.
            var plain = Core.SpeechTextSanitizer.PlainSpeech(spoken);
            if (plain.Length == 0) plain = spoken;

            var buffer = new Core.SpeechPlaybackBuffer
            {
                Speed = _settings.ReadAloudSpeed,
                EstimatedTotalSeconds = plain.Length * Core.ReadAloud.EstimatedSecondsPerCharacter,
            };
            _buffer = buffer;
            var voice = _settings.ReadAloudVoiceFor(model.Provider);
            int failed = await _tts.SynthesizeAsync(model, voice, plain, pcm =>
            {
                buffer.Append(pcm);
                _ui.Post(_ => { if (run == _run) StartPlaybackOnce(buffer); }, null);
            }, ct);
            buffer.MarkComplete();
            if (failed > 0) Core.Log.Error($"Read aloud: {failed} chunk(s) failed, played with a gap");
        }
        catch (OperationCanceledException) when (run != _run || ct.IsCancellationRequested)
        {
            // Stopped by the user.
        }
        catch (SelectionUnreadableException)
        {
            if (run != _run) return;
            Stop();
            Sounds.Error();
            _notify("Vorlesen", "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", ToolTipIcon.Warning, null);
        }
        catch (Core.GeminiException e)
        {
            if (run != _run) return;
            Core.Log.Error($"Read aloud: {e.Provider} {e.Kind}");
            Stop();
            Sounds.Error();
            Action? click = e.Kind is Core.GeminiErrorKind.NoKey or Core.GeminiErrorKind.InvalidKey ? _showSettings : null;
            _notify("Vorlesen fehlgeschlagen", e.Message, ToolTipIcon.Error, click);
        }
        catch (Exception e)
        {
            if (run != _run) return;
            Core.Log.Error("Read aloud failed", e);
            Stop();
            Sounds.Error();
            _notify("Vorlesen fehlgeschlagen", "Der Text konnte nicht vorgelesen werden. Details stehen im Protokoll.", ToolTipIcon.Error, null);
        }
    }

    /// <summary>First audio: switch to speaking and start the device. Later chunks only grow the buffer.</summary>
    private void StartPlaybackOnce(Core.SpeechPlaybackBuffer buffer)
    {
        if (_player != null || _buffer != buffer) return;
        try
        {
            _player = new TtsPlayer(buffer);
        }
        catch (Exception e)
        {
            // No output device (unplugged headset, broken driver).
            Core.Log.Error("Read aloud: audio output failed", e);
            Stop();
            Sounds.Error();
            _notify("Vorlesen fehlgeschlagen", "Kein Audioausgang gefunden. Bitte Lautsprecher oder Kopfhörer prüfen.", ToolTipIcon.Error, null);
            return;
        }
        int run = _run;
        _player.Finished += error =>
        {
            if (run != _run) return;
            if (error != null) Core.Log.Error("Read aloud: playback stopped", error);
            else Core.Log.Info("Read aloud: finished");
            Stop();
        };
        _player.Play();
        _overlay.ShowSpeaking(buffer, () => _player?.IsPaused == true);
        SetPhase(Phase.Speaking);
    }

    private void OnCommand(ReadAloudOverlay.Command command)
    {
        switch (command)
        {
            case ReadAloudOverlay.Command.Stop:
                Core.Log.Info("Read aloud: stopped from the card");
                Stop();
                break;
            case ReadAloudOverlay.Command.PlayPause:
                _player?.TogglePause();
                break;
            case ReadAloudOverlay.Command.Back:
                _buffer?.SkipBy(-10);
                break;
            case ReadAloudOverlay.Command.Forward:
                _buffer?.SkipBy(10);
                break;
            case ReadAloudOverlay.Command.Speed:
                if (_buffer == null) break;
                // Mac cycleReadAloudSpeed: the new speed is also the setting for the next time.
                var next = Core.ReadAloud.NextSpeed(_buffer.Speed);
                _buffer.Speed = next;
                _settings.ReadAloudSpeed = next;
                _save();
                break;
        }
    }

    /// <summary>
    /// Mac maybeRewriteForSpeech: only with the switch on, only when the text is not plain prose (prose comes back
    /// unchanged after a 2–6 s round trip), with the Dictate Prompt text model. Any failure reads the original.
    /// </summary>
    private async Task<string> MaybeRewriteAsync(string text, CancellationToken ct)
    {
        if (!_settings.ReadAloudSmartRewrite) return text;
        if (Core.SpeechTextSanitizer.LooksLikePlainProse(text))
        {
            Core.Log.Info("Read aloud: plain prose, no rewrite");
            return text;
        }
        Func<Task<string>>? call = _settings.EffectiveTextEngine switch
        {
            Core.Engine.Gemini when !string.IsNullOrWhiteSpace(_settings.ApiKey) =>
                () => _gemini.CompleteTextAsync(Core.ReadAloud.DefaultRewritePrompt, text, ct),
            Core.Engine.OpenAI when !string.IsNullOrWhiteSpace(_settings.OpenAiKey) =>
                () => _openAi.CompleteTextAsync(Core.ReadAloud.DefaultRewritePrompt, text, ct),
            // The local model would add seconds to a minute before the first word; the Mac skips on-device too.
            _ => null,
        };
        if (call == null)
        {
            Core.Log.Info($"Read aloud: no rewrite with text engine {_settings.EffectiveTextEngine}");
            return text;
        }
        try
        {
            var rewritten = (await call()).Trim();
            if (rewritten.Length == 0) return text;
            if (!Core.ReadAloud.RewriteStaysWithinLengthBudget(rewritten, text))
            {
                Core.Log.Info($"Read aloud: rewrite discarded, {text.Length} → {rewritten.Length} chars is too long");
                return text;
            }
            Core.Log.Info($"Read aloud: rewrote {text.Length} → {rewritten.Length} chars");
            return rewritten;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Core.Log.Error($"Read aloud: rewrite failed ({e.GetType().Name}), reading the original");
            return text;
        }
    }

    private string? MissingKeyMessage(Core.TtsModel model) => model.Provider switch
    {
        Core.TtsProvider.Gemini when string.IsNullOrWhiteSpace(_settings.ApiKey) =>
            "Trag unter Einstellungen → Konten deinen Google-API-Schlüssel ein, um Vorlesen zu nutzen.",
        Core.TtsProvider.OpenAI when string.IsNullOrWhiteSpace(_settings.OpenAiKey) =>
            "Trag unter Einstellungen → Konten deinen OpenAI-API-Schlüssel ein, um Vorlesen zu nutzen, oder wähle unter "
            + "Einstellungen → Vorlesen ein anderes Sprachmodell.",
        _ => null,
    };

    private void SetPhase(Phase phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _overlay.Dispose();
    }
}
