namespace WhisperShortcut;

internal enum TakePhase { Recording, Processing }

/// <summary>
/// A recording whose processing failed for a reason a second try can fix (network, quota, local model), or kept audio
/// that runs again (cancelled recording, "Nochmal ohne Vorlage", a take recovered after a crash). Replays are always
/// clipboard-only: the window the take was made for is whatever the user did since. <see cref="Kind"/> is never Live:
/// a live dictation replays as a plain dictation of its audio. Built from a running take by <see cref="Take.ToReplay"/>.
/// </summary>
internal sealed record FailedRun(byte[] Wav, Core.RecordingKindName Kind, double Seconds, string StopKey = "",
    string? Selection = null, string? QuickAction = null, IntPtr Target = default, Core.DictationTemplate? Template = null,
    Core.ScreenshotImage? Screenshot = null, bool NoNewTextTemplate = false);

/// <summary>
/// One dictation / prompt / template / live run, from the hotkey press to the end of its processing. Everything the
/// run owns lives here, so there is one place that sets it up (<c>TrayApp.StartRecording</c>, <see cref="Replay"/>) and
/// one that tears it down: <see cref="Dispose"/>. Idle is "no take". Only touched on the UI thread (the live feed has
/// its own lock, see <see cref="LiveRun"/>).
/// </summary>
internal sealed class Take : IDisposable
{
    private readonly ClipboardPaster _paster;
    private CancellationTokenSource? _selectionCts;
    private bool _disposed;

    public Take(Core.RecordingKindName kind, string stopKey, ClipboardPaster paster)
    {
        Kind = kind;
        StopKey = stopKey;
        _paster = paster;
    }

    /// <summary>Live is its own kind, but the hotkeys treat it as part of Diktieren (<see cref="Mode"/>).</summary>
    public Core.RecordingKindName Kind { get; }

    /// <summary>The hotkey family: what the engines, the overlay and "same key stops" see. Live is Dictate.</summary>
    public Mode Mode => ModeOf(Kind);

    public static Mode ModeOf(Core.RecordingKindName kind) => kind switch
    {
        Core.RecordingKindName.Prompt => Mode.Prompt,
        Core.RecordingKindName.Template => Mode.Template,
        _ => Mode.Dictate,
    };

    public static Core.RecordingKindName KindOf(Mode mode) => mode switch
    {
        Mode.Prompt => Core.RecordingKindName.Prompt,
        Mode.Template => Core.RecordingKindName.Template,
        _ => Core.RecordingKindName.Dictate,
    };

    public TakePhase Phase { get; set; } = TakePhase.Recording;

    /// <summary>The stored hotkey that started the run; shown in the overlay hint and the status text.</summary>
    public string StopKey { get; }

    /// <summary>The window in front when the recording started: the only paste target, for a replay too.</summary>
    public IntPtr PasteTarget { get; set; }

    /// <summary>A copy taken at the start of a template run: editing the settings meanwhile must not change this run.</summary>
    public Core.DictationTemplate? Template { get; init; }

    /// <summary>"Nochmal ohne Vorlage": this Dictate Prompt run leaves out the "Vorlage für neue Texte" too (the Mac has none there).</summary>
    public bool NoNewTextTemplate { get; init; }

    /// <summary>A replay of kept audio: the result is copied, never pasted.</summary>
    public bool ClipboardOnly { get; init; }

    /// <summary>The live session of a Live take; disposed with it.</summary>
    public LiveRun? Live { get; init; }

    /// <summary>Bumped by every recording that starts; right Ctrl remembers the one it started (Mac fnStartedRecordingID).</summary>
    public long Serial { get; set; }

    public Core.IAudioSource? Audio { get; set; }

    /// <summary>The selection copied while recording (prompt and template runs); null once cancelled.</summary>
    public Task<string?>? Selection { get; private set; }

    public string? QuickAction { get; set; }
    public Core.ScreenshotImage? Shot { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public string BusyStatus { get; set; } = Core.UserCopy.Processing;

    /// <summary>Words of the reply nobody dictated, shown after the paste.</summary>
    public IReadOnlyList<string> Unsupported { get; set; } = Array.Empty<string>();

    /// <summary>Quit keeps the crash checkpoint of a running recording so the next start can recover it; every other end discards it.</summary>
    public bool KeepCheckpoint { get; set; }

    /// <summary>The "mode" string of the crash checkpoint (<see cref="Core.RecordingKind"/>).</summary>
    public string CheckpointMode => Core.RecordingKind.Format(Kind, Template?.Id);

    /// <summary>A take that runs kept audio from idle (<paramref name="shotAllowed"/>: screenshots may be off or offline since).</summary>
    public static Take Replay(FailedRun run, ClipboardPaster paster, bool shotAllowed) => new(run.Kind, run.StopKey, paster)
    {
        Template = run.Template,
        NoNewTextTemplate = run.NoNewTextTemplate,
        ClipboardOnly = true,
        QuickAction = run.QuickAction,
        Shot = shotAllowed ? run.Screenshot : null,
        PasteTarget = run.Target,
        Phase = TakePhase.Processing,
    };

    /// <summary>What a retry needs. The one place a <see cref="FailedRun"/> is built from a running take.</summary>
    public FailedRun ToReplay(byte[] wav, double seconds, string? selection) =>
        new(wav, Kind == Core.RecordingKindName.Live ? Core.RecordingKindName.Dictate : Kind, seconds, StopKey, selection,
            QuickAction, PasteTarget, Template, Shot, NoNewTextTemplate);

    /// <summary>Copies the user's selection right away, while it is still there; it runs while recording.</summary>
    public void StartSelectionCopy()
    {
        _selectionCts = new CancellationTokenSource();
        Selection = _paster.CopySelectionAsync(_selectionCts.Token);
    }

    /// <summary>
    /// THE teardown of a run, whichever way it ends (done, failed, cancelled, Esc, failed start, quit). Idempotent.
    /// Stops the microphone, the selection copy (no Ctrl+C into the user's window after the cancel) and its late-copy
    /// watch (a late copy is no longer ours to restore), and the request. What a caller may do differently stays in the
    /// caller: keeping the audio for a retry, the clipboard, the overlay, the sounds.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (Audio as MicAudioSource)?.Cancel(discardCheckpoint: !KeepCheckpoint);
        Audio = null;
        Live?.Dispose();
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;
        Selection = null;
        _paster.CancelLateWatch();
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
        Shot = null; // a failed run keeps its copy in the FailedRun
    }
}
