using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>
/// Live Meeting on Windows: the tray submenu, the meeting hotkey, the capture and the session. Kept out
/// of <see cref="TrayApp"/> on purpose, which only hands in what a meeting needs (how to transcribe a
/// chunk, how to summarize, how to tell the user) and adds the menu item.
/// A meeting runs beside dictation: both may record the microphone at the same time.
/// Everything here runs on the UI thread; session and capture events are posted back to it.
/// </summary>
internal sealed class MeetingController : IDisposable
{
    private readonly Core.Settings _settings;
    private readonly SynchronizationContext _ui;
    private readonly Func<byte[], CancellationToken, Task<string>> _transcribe;
    private readonly Func<string, CancellationToken, Task<string>> _summarize;
    private readonly Func<string?> _unavailable;
    private readonly Action<string, string, ToolTipIcon, Action?> _notify;
    private readonly Action _save;
    private readonly Func<IReadOnlyCollection<string>> _otherHotkeys;
    private readonly Action<bool> _suspendHotkeys;
    private readonly bool _fakeAudio;

    private readonly Core.MeetingStore _store = new(Core.MeetingStore.DefaultRoot);
    private readonly MeetingOverlay _overlay = new();
    private readonly ToolStripMenuItem _startStop;
    private readonly ToolStripMenuItem _modeAuto;
    private readonly ToolStripMenuItem _modeMic;
    private readonly ToolStripMenuItem _modeMix;
    private readonly ToolStripMenuItem _summaryItem;
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly ToolStripMenuItem _openFolder;

    private Core.LiveMeetingSession? _session;
    private MeetingAudioCapture? _capture;
    private System.Windows.Forms.Timer? _fakeTimer;
    private DateTime _startedUtc;
    private bool _mixes;
    private bool _stopping;
    private bool _retrying;
    private bool _failureShown;
    private string? _lastFolder;

    /// <param name="transcribe">One chunk WAV → cleaned transcript text ("" when nothing was said); throws on failure.</param>
    /// <param name="summarize">Transcript → Markdown summary with the Dictate Prompt model.</param>
    /// <param name="unavailable">Why a meeting cannot start right now (no key, offline models missing), or null.</param>
    /// <param name="otherHotkeys">Stored hotkeys of dictation, Dictate Prompt and glossary, for the duplicate check.</param>
    /// <param name="suspendHotkeys">true while the hotkey dialog records keys, false to register them again.</param>
    /// <param name="fakeAudio">VM test mode (--fake-gemini): a silent-free synthetic tone instead of the microphone.</param>
    public MeetingController(Core.Settings settings, SynchronizationContext ui,
        Func<byte[], CancellationToken, Task<string>> transcribe, Func<string, CancellationToken, Task<string>> summarize,
        Func<string?> unavailable, Action<string, string, ToolTipIcon, Action?> notify, Action save,
        Func<IReadOnlyCollection<string>> otherHotkeys, Action<bool> suspendHotkeys, bool fakeAudio)
    {
        _settings = settings;
        _ui = ui;
        _transcribe = transcribe;
        _summarize = summarize;
        _unavailable = unavailable;
        _notify = notify;
        _save = save;
        _otherHotkeys = otherHotkeys;
        _suspendHotkeys = suspendHotkeys;
        _fakeAudio = fakeAudio;

        _startStop = new ToolStripMenuItem("Meeting starten", null, (_, _) => Toggle()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _modeAuto = new ToolStripMenuItem("Automatisch (Kopfhörer: mit PC-Ton)", null, (_, _) => SetMode(Core.MeetingAudioPreference.Automatic));
        _modeMix = new ToolStripMenuItem("Mikrofon + PC-Ton", null, (_, _) => SetMode(Core.MeetingAudioPreference.MicrophoneAndSystem));
        _modeMic = new ToolStripMenuItem("Nur Mikrofon", null, (_, _) => SetMode(Core.MeetingAudioPreference.MicrophoneOnly));
        _summaryItem = new ToolStripMenuItem("Am Ende zusammenfassen", null, (_, _) => ToggleSummary());
        _hotkeyItem = new ToolStripMenuItem("Tastenkürzel festlegen …", null, (_, _) => PickHotkey());
        _retryItem = new ToolStripMenuItem("", null, (_, _) => _ = RetryBacklogAsync(announce: true)) { Visible = false };
        _openFolder = new ToolStripMenuItem("Meeting-Ordner öffnen", null, (_, _) => OpenFolder());

        MenuItem = new ToolStripMenuItem("Meeting");
        MenuItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            _startStop, new ToolStripSeparator(),
            new ToolStripMenuItem("Aufnehmen:") { Enabled = false }, _modeAuto, _modeMix, _modeMic, new ToolStripSeparator(),
            _summaryItem, _hotkeyItem, new ToolStripSeparator(),
            _retryItem, _openFolder,
        });
        MenuItem.DropDownOpening += (_, _) => RefreshMenu();
        _overlay.StopClicked += () => _ = StopAsync();
        RefreshMenu();
    }

    /// <summary>The "Meeting" submenu for the tray menu.</summary>
    public ToolStripMenuItem MenuItem { get; }

    /// <summary>A meeting records or is finishing.</summary>
    public bool IsActive => _session != null;

    /// <summary>Hotkey and "Meeting starten / beenden".</summary>
    public void Toggle()
    {
        if (_stopping) return;
        if (_session == null) Start();
        else _ = StopAsync();
    }

    /// <summary>
    /// App start: a meeting still marked as recording belongs to a run that was killed. Its transcript
    /// and the audio not yet transcribed are on disk; say so and transcribe what is left.
    /// </summary>
    public void OnStartup()
    {
        try
        {
            var interrupted = _store.RecoverInterrupted();
            foreach (var m in interrupted) Core.Log.Info($"Meeting: {m.Id} was interrupted, {m.PendingAudioChunks} chunk(s) of audio kept");
            if (interrupted.Count > 0)
            {
                var last = interrupted[0];
                _lastFolder = last.Folder;
                _notify("Meeting wurde unterbrochen",
                    "WhisperShortcut wurde während eines Meetings beendet. Das bis dahin Transkribierte ist gespeichert"
                    + (last.PendingAudioChunks > 0 ? ", der restliche Ton wird jetzt nachträglich transkribiert." : "."),
                    ToolTipIcon.Warning, () => OpenFolder(last.Folder));
            }
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: recovery at start failed", e);
        }
        RefreshMenu();
        if (_unavailable() == null) _ = RetryBacklogAsync(announce: false);
    }

    // ---------------- start / stop ----------------

    private void Start()
    {
        if (_unavailable() is { } reason)
        {
            Core.Log.Info("Meeting: start refused, not available");
            Sounds.Error();
            _notify("Meeting nicht möglich", reason, ToolTipIcon.Warning, null);
            return;
        }

        var preference = Core.MeetingAudioRouting.Parse(_settings.MeetingAudioMode);
        var output = _fakeAudio ? (HasDevice: false, FormFactor: (int?)null, Name: (string?)null) : MeetingAudioCapture.DefaultOutput();
        _mixes = Core.MeetingAudioRouting.MixesSystemAudio(preference, output.HasDevice, output.FormFactor);
        Core.Log.Info($"Meeting: start, preference {Core.MeetingAudioRouting.Name(preference)}, output form factor {output.FormFactor?.ToString() ?? "none"}, mix {_mixes}");

        var session = new Core.LiveMeetingSession(_store, _transcribe, Core.MeetingAudioRouting.MetadataName(_mixes));
        // One more try after 5 s: GeminiClient already retries a chunk 5–6 times with backoff, and a longer wait
        // here would let the queue fall behind a one-minute chunk cadence during an outage (the audio is on disk anyway).
        session.RetryDelays = new[] { TimeSpan.FromSeconds(5) };
        try
        {
            session.Start();
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: creating the meeting folder failed", e);
            session.Dispose();
            Sounds.Error();
            _notify("Meeting nicht möglich", "Der Meeting-Ordner konnte nicht angelegt werden. Details stehen im Protokoll.", ToolTipIcon.Error, null);
            return;
        }

        Func<float>? level = null;
        if (_fakeAudio)
        {
            StartFakeAudio(session);
        }
        else
        {
            var capture = new MeetingAudioCapture(_settings.Microphone, _mixes, samples => session.WriteAudio(samples));
            capture.DeviceLost += () => _ui.Post(_ =>
            {
                if (_capture != capture) return;
                _notify("Mikrofon getrennt", "Das Meeting wurde beendet. Das Bisherige wird gespeichert.", ToolTipIcon.Warning, null);
                _ = StopAsync();
            }, null);
            try
            {
                capture.Start();
            }
            catch (Exception e)
            {
                Core.Log.Error("Meeting: capture start failed", e);
                capture.Dispose();
                _ = session.StopAsync(null, CancellationToken.None).ContinueWith(_ => session.Dispose(), TaskScheduler.Default);
                Sounds.Error();
                _notify("Aufnahme nicht möglich", e.Message, ToolTipIcon.Error, null);
                return;
            }
            _capture = capture;
            level = () => capture.Level;
            if (_mixes && !capture.SystemAudioActive)
                _notify("PC-Ton fehlt", "Der Ton des PCs kann nicht mit aufgenommen werden, das Meeting läuft nur mit dem Mikrofon.", ToolTipIcon.Warning, null);
        }

        _session = session;
        _lastFolder = session.Meeting.Folder;
        _failureShown = false;
        session.Changed += () => _ui.Post(_ => UpdateStatus(), null);
        session.ChunkFailed += e => _ui.Post(_ => OnChunkFailed(e), null);
        _startedUtc = DateTime.UtcNow;
        Sounds.Start();
        _overlay.ShowRecording(_startedUtc, level);
        UpdateStatus();
        RefreshMenu();
    }

    private async Task StopAsync()
    {
        var session = _session;
        if (session == null || _stopping) return;
        _stopping = true;
        Core.Log.Info($"Meeting: stop after {(DateTime.UtcNow - _startedUtc).TotalMinutes:0.0} min");
        Sounds.Stop();
        StopAudio();
        _overlay.ShowFinishing(session.QueuedChunks > 0 ? "Letzte Abschnitte werden transkribiert …" : "Wird gespeichert …");
        RefreshMenu();

        Core.MeetingStopResult? result = null;
        try
        {
            // Five minutes for the last chunks; whatever is not through by then stays as audio for a later retry.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            result = await session.StopAsync(_settings.MeetingSummary ? SummarizeAsync : null, cts.Token);
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: stop failed", e);
        }
        finally
        {
            session.Dispose();
            _session = null;
            _stopping = false;
            _overlay.HideOverlay();
            RefreshMenu();
        }

        var folder = result?.Meeting.Folder ?? _lastFolder;
        string text;
        var icon = ToolTipIcon.Info;
        if (result == null)
        {
            text = "Beim Abschließen ist etwas schiefgelaufen. Was transkribiert wurde, liegt im Meeting-Ordner.";
            icon = ToolTipIcon.Warning;
        }
        else if (result.PendingChunks > 0)
        {
            text = $"{Count(result.PendingChunks)} noch nicht transkribiert, der Ton ist gespeichert. Erneut versuchen: Symbol → Meeting.";
            icon = ToolTipIcon.Warning;
        }
        else if (result.TranscribedChunks == 0)
        {
            text = "Im Meeting wurde keine Sprache erkannt.";
        }
        else
        {
            text = _settings.MeetingSummary && !result.SummarySaved
                ? "Transkript gespeichert. Die Zusammenfassung konnte nicht erstellt werden."
                : _settings.MeetingSummary ? "Transkript und Zusammenfassung gespeichert." : "Transkript gespeichert.";
        }
        _notify("Meeting beendet", text, icon, () => OpenFolder(folder));
    }

    /// <summary>Summary as Markdown with a heading, so summary.md reads on its own.</summary>
    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        _ui.Post(_ => _overlay.ShowFinishing("Zusammenfassung wird erstellt …"), null);
        var raw = (await _summarize(transcript, ct)).Trim();
        return raw.Length == 0 ? "" : $"# Zusammenfassung\n\n{raw}\n";
    }

    private void StopAudio()
    {
        _fakeTimer?.Dispose();
        _fakeTimer = null;
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try { capture.Stop(); }
        catch (Exception e) { Core.Log.Error("Meeting: capture stop failed", e); }
        capture.Dispose();
    }

    /// <summary>
    /// Quit from the tray: end the meeting properly instead of leaving it "recording". Waits a few
    /// seconds at most; what is not transcribed by then stays as audio and is retried at the next start.
    /// </summary>
    public void Shutdown()
    {
        var session = _session;
        StopAudio();
        if (session == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var stop = Task.Run(() => session.StopAsync(null, cts.Token));
            if (!stop.Wait(TimeSpan.FromSeconds(5))) Core.Log.Warn("Meeting: stop at quit timed out, audio kept");
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: stop at quit failed", e);
        }
        session.Dispose();
        _session = null;
    }

    // ---------------- status ----------------

    private void UpdateStatus()
    {
        var s = _session;
        if (s == null || _stopping) return;
        string mode = Core.MeetingAudioRouting.Label(_mixes && (_fakeAudio || _capture?.SystemAudioActive == true));
        if (s.PendingChunks > 0)
        {
            _overlay.SetStatus($"{mode} · {Count(s.PendingChunks)} wartet auf neuen Versuch", warn: true);
            return;
        }
        string done = s.TranscribedChunks == 0 ? "noch kein Text" : $"{Count(s.TranscribedChunks)} transkribiert";
        _overlay.SetStatus($"{mode} · {done}");
    }

    private void OnChunkFailed(Exception e)
    {
        UpdateStatus();
        // One balloon per meeting: a dead network would otherwise bring one every minute.
        if (_failureShown) return;
        _failureShown = true;
        string reason = e is Core.GeminiException g ? g.Message : "Details stehen im Protokoll.";
        _notify("Meeting: Abschnitt nicht transkribiert",
            "Der Ton ist gespeichert und wird später erneut versucht. " + reason, ToolTipIcon.Warning, null);
    }

    private static string Count(int chunks) => chunks == 1 ? "1 Abschnitt" : $"{chunks} Abschnitte";

    // ---------------- menu ----------------

    private void RefreshMenu()
    {
        bool active = _session != null;
        _startStop.Text = _stopping ? "Meeting wird abgeschlossen …" : active ? "Meeting beenden" : "Meeting starten";
        if (!string.IsNullOrEmpty(_settings.MeetingHotkey) && !_stopping) _startStop.Text += $"  ({Hotkey.Display(_settings.MeetingHotkey)})";
        _startStop.Enabled = !_stopping;
        var preference = Core.MeetingAudioRouting.Parse(_settings.MeetingAudioMode);
        _modeAuto.Checked = preference == Core.MeetingAudioPreference.Automatic;
        _modeMix.Checked = preference == Core.MeetingAudioPreference.MicrophoneAndSystem;
        _modeMic.Checked = preference == Core.MeetingAudioPreference.MicrophoneOnly;
        // The running meeting keeps what it started with; the choice applies to the next one.
        _modeAuto.Enabled = _modeMix.Enabled = _modeMic.Enabled = !active;
        _summaryItem.Checked = _settings.MeetingSummary;
        _hotkeyItem.Text = string.IsNullOrEmpty(_settings.MeetingHotkey)
            ? "Tastenkürzel festlegen …"
            : $"Tastenkürzel: {Hotkey.Display(_settings.MeetingHotkey)} …";

        int pending = 0;
        try
        {
            foreach (var id in _store.IdsWithPendingAudio())
                if (id != _session?.Meeting.Id) pending += _store.ListPendingAudio(id).Count;
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: reading the backlog failed", e);
        }
        _retryItem.Visible = pending > 0;
        _retryItem.Enabled = !_retrying;
        _retryItem.Text = _retrying ? "Ton wird nachträglich transkribiert …" : $"Nicht transkribierten Ton erneut versuchen ({Count(pending)})";
        _openFolder.Enabled = Directory.Exists(_store.Root);
    }

    private void SetMode(Core.MeetingAudioPreference preference)
    {
        _settings.MeetingAudioMode = Core.MeetingAudioRouting.Name(preference);
        _save();
        RefreshMenu();
    }

    private void ToggleSummary()
    {
        _settings.MeetingSummary = !_settings.MeetingSummary;
        _save();
        RefreshMenu();
    }

    private void PickHotkey()
    {
        // Off while the dialog records keys: pressing Ctrl+Shift+D there must not start a dictation.
        _suspendHotkeys(true);
        try
        {
            var picked = MeetingHotkeyDialog.Ask(_settings.MeetingHotkey, _otherHotkeys());
            if (picked != null && picked != _settings.MeetingHotkey)
            {
                _settings.MeetingHotkey = picked;
                _save();
                Core.Log.Info($"Meeting: hotkey {(picked.Length == 0 ? "removed" : "set")}");
            }
        }
        finally
        {
            // Registering again includes the new key; a taken one is reported by TrayApp like any other.
            _suspendHotkeys(false);
            RefreshMenu();
        }
    }

    private void OpenFolder() => OpenFolder(_session?.Meeting.Folder ?? _lastFolder);

    private void OpenFolder(string? folder)
    {
        var path = folder != null && Directory.Exists(folder) ? folder : _store.Root;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: opening the folder failed", e);
        }
    }

    // ---------------- backlog ----------------

    /// <summary>Audio of earlier meetings that could not be transcribed: one more try each, oldest first.</summary>
    private async Task RetryBacklogAsync(bool announce)
    {
        if (_retrying) return;
        if (_unavailable() is { } reason)
        {
            if (announce) _notify("Erneuter Versuch nicht möglich", reason, ToolTipIcon.Warning, null);
            return;
        }
        _retrying = true;
        RefreshMenu();
        int recovered = 0, still = 0;
        Exception? error = null;
        try
        {
            foreach (var id in _store.IdsWithPendingAudio())
            {
                if (id == _session?.Meeting.Id) continue; // the running meeting retries its own audio
                var r = await Core.LiveMeetingSession.RetryPendingAsync(_store, id, _transcribe, CancellationToken.None);
                recovered += r.Recovered;
                still += r.StillPending;
                error ??= r.FirstError;
            }
        }
        catch (Exception e)
        {
            Core.Log.Error("Meeting: backlog retry failed", e);
            error ??= e;
        }
        finally
        {
            _retrying = false;
            RefreshMenu();
        }
        Core.Log.Info($"Meeting: backlog retry, {recovered} recovered, {still} still pending");
        if (!announce && recovered == 0) return;
        if (still == 0 && recovered > 0)
            _notify("Meeting-Transkript vollständig", $"{Count(recovered)} nachträglich transkribiert.", ToolTipIcon.Info, () => OpenFolder());
        else if (announce || still > 0)
            _notify("Noch nicht alles transkribiert",
                $"{Count(still)} weiterhin offen{(recovered > 0 ? $", {Count(recovered)} geschafft" : "")}. Der Ton bleibt gespeichert. "
                + (error is Core.GeminiException g ? g.Message : ""),
                ToolTipIcon.Warning, null);
    }

    // ---------------- test mode ----------------

    /// <summary>
    /// --fake-gemini in the VM: no microphone needed. Feeds a quiet tone with a pause every few seconds,
    /// so chunks rotate and the fake transcription fills the transcript.
    /// </summary>
    private void StartFakeAudio(Core.LiveMeetingSession session)
    {
        long sample = 0;
        _fakeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _fakeTimer.Tick += (_, _) =>
        {
            var buffer = new short[1600];
            for (int i = 0; i < buffer.Length; i++, sample++)
            {
                bool talking = sample / 16000 % 14 < 12; // 12 s "speech", 2 s pause
                buffer[i] = talking ? (short)(6000 * Math.Sin(2 * Math.PI * 220 * sample / 16000.0)) : (short)0;
            }
            session.WriteAudio(buffer);
        };
        _fakeTimer.Start();
    }

    public void Dispose()
    {
        Shutdown();
        _overlay.Dispose();
    }
}
