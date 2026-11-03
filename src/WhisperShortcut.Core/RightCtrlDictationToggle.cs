namespace WhisperShortcut.Core;

/// <summary>What <see cref="RightCtrlDictationToggle"/> drives. Mirrors the macOS <c>FnDictationToggleDelegate</c>.</summary>
public interface IRightCtrlDictationDelegate
{
    /// <summary>Attempt to start a Diktat recording. True only if a recording actually started.</summary>
    bool Start();

    /// <summary>Stop the recording and transcribe (normal stop path).</summary>
    void Stop();

    /// <summary>Stop the recording and drop the audio (right Ctrl used as a modifier right after it started).</summary>
    void Discard();

    /// <summary>
    /// Whether the dictation recording is still running. It can be stopped elsewhere (tray, Alt+1), in which
    /// case the next press must start fresh instead of trying to stop a recording that no longer exists.
    /// </summary>
    bool IsRecording();

    /// <summary>Whether a dictation transcription is processing (cancellable with a press).</summary>
    bool IsProcessing();

    /// <summary>Cancel the in-flight transcription (same behavior as the dictation hotkey).</summary>
    void CancelProcessing();
}

/// <summary>
/// Right Ctrl dictation, the Windows stand-in for the Mac Fn key (port of <c>FnDictationToggle.swift</c>).
/// A pure toggle:
/// <list type="bullet">
/// <item>First press: starts a recording, which keeps running after the key is released.</item>
/// <item>Second press: stops the recording and transcribes it.</item>
/// <item>Press while transcribing: cancels it, but not within <see cref="CancelGuardWindowMs"/> of the stop,
/// so the press that starts the next dictation cannot destroy the one just finished.</item>
/// </list>
/// No hold-to-talk: a press-duration threshold cannot tell a slow toggle press from a hold.
/// Both edges resolve on key release, because right Ctrl is also a real modifier: only when it comes up
/// without a regular key in between is it certain the user meant "dictate" and not Ctrl+C. The recording
/// still starts on key down so the overlay appears at once; it is discarded if a regular key follows.
/// Platform-free: the low-level keyboard hook calls <see cref="KeyDown"/>, <see cref="KeyUp"/> and
/// <see cref="OtherKeyDown"/>; the clock is injected for the guard window.
/// </summary>
public sealed class RightCtrlDictationToggle
{
    /// <summary>Lifecycle of a right-Ctrl-initiated recording.</summary>
    public enum State
    {
        /// <summary>No right-Ctrl-initiated recording.</summary>
        Idle,
        /// <summary>Key down, recording just started. Release confirms; a regular key discards.</summary>
        Starting,
        /// <summary>Recording runs with the key up, waiting for the stop press.</summary>
        Recording,
        /// <summary>Key down again during a recording. Release stops; a regular key keeps it running.</summary>
        Stopping,
        /// <summary>Key down while processing. Release cancels; a regular key keeps the transcription.</summary>
        Cancelling,
    }

    /// <summary>
    /// How long the cancel gesture stays disarmed after a recording is handed off. On the Mac every one of
    /// 32 logged cancellations fired within 2.2 s of the stop: it was always the next dictation, never an abort.
    /// </summary>
    public const long CancelGuardWindowMs = 2000;

    private readonly IRightCtrlDictationDelegate _delegate;
    private readonly Func<long> _nowMs;
    private long? _transcriptionStartedAt;

    /// <param name="delegate">What starts, stops and cancels.</param>
    /// <param name="nowMs">Monotonic clock in milliseconds (e.g. <c>Environment.TickCount64</c>).</param>
    public RightCtrlDictationToggle(IRightCtrlDictationDelegate @delegate, Func<long> nowMs)
    {
        _delegate = @delegate;
        _nowMs = nowMs;
    }

    /// <summary>Setting „Rechte Strg-Taste diktiert“. When false, key-down presses do nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Current state, for logging and tests.</summary>
    public State Current { get; private set; } = State.Idle;

    /// <summary>
    /// True while the hook should report regular keys via <see cref="OtherKeyDown"/> (right Ctrl is held
    /// and the press is still undecided). Mirrors the Mac keyDown monitors that exist only during a hold.
    /// </summary>
    public bool WatchingOtherKeys => Current is State.Starting or State.Stopping or State.Cancelling;

    /// <summary>Right Ctrl went down. A repeat while already down is a no-op.</summary>
    public void KeyDown()
    {
        if (!Enabled) return;
        switch (Current)
        {
            case State.Idle:
                if (_delegate.IsProcessing())
                {
                    if (IsWithinCancelGuardWindow())
                    {
                        // Almost certainly the next dictation, not an abort: keep the transcription.
                        Log.Info("SHORTCUTS: Right Ctrl pressed right after stopping — ignoring, transcription kept");
                        return;
                    }
                    // Release decides: a regular key in between means Ctrl was a modifier.
                    Current = State.Cancelling;
                }
                else
                {
                    StartRecording();
                }
                break;
            case State.Recording:
                if (_delegate.IsRecording())
                {
                    // Release decides: a regular key in between means Ctrl was a modifier.
                    Current = State.Stopping;
                }
                else
                {
                    // Stopped elsewhere; this press starts fresh.
                    Current = State.Idle;
                    KeyDown();
                }
                break;
            case State.Starting:
            case State.Stopping:
            case State.Cancelling:
                break; // Key repeat without a release in between.
        }
    }

    /// <summary>Right Ctrl went up.</summary>
    public void KeyUp()
    {
        switch (Current)
        {
            case State.Idle:
            case State.Recording:
                break;
            case State.Starting:
                // However long the key was held, the recording keeps running until the next press.
                Current = State.Recording;
                break;
            case State.Stopping:
                Log.Info("SHORTCUTS: Right Ctrl pressed again — stopping recording");
                Current = State.Idle;
                _transcriptionStartedAt = _nowMs();
                _delegate.Stop();
                break;
            case State.Cancelling:
                Log.Info("SHORTCUTS: Right Ctrl pressed during transcription — cancelling");
                Current = State.Idle;
                _transcriptionStartedAt = null;
                _delegate.CancelProcessing();
                break;
        }
    }

    /// <summary>A regular (non-modifier) key went down while right Ctrl is held: Ctrl is a modifier.</summary>
    public void OtherKeyDown()
    {
        switch (Current)
        {
            case State.Idle:
            case State.Recording:
                break;
            case State.Starting:
                // The recording only exists because of this press: abort it instead of transcribing the accident.
                Log.Info("SHORTCUTS: Key pressed while Right Ctrl held — Ctrl is a modifier, discarding recording");
                Current = State.Idle;
                _delegate.Discard();
                break;
            case State.Stopping:
                // The user started this recording deliberately earlier: keep it running.
                Log.Info("SHORTCUTS: Key pressed while Right Ctrl held — Ctrl is a modifier, keeping recording");
                Current = State.Recording;
                break;
            case State.Cancelling:
                Log.Info("SHORTCUTS: Key pressed while Right Ctrl held — Ctrl is a modifier, keeping transcription");
                Current = State.Idle;
                break;
        }
    }

    private void StartRecording()
    {
        if (!_delegate.Start()) return;
        Log.Info("SHORTCUTS: Right Ctrl pressed — recording started, press Right Ctrl again to stop");
        // A new recording only starts when nothing is processing, so any pending stamp is stale.
        _transcriptionStartedAt = null;
        Current = State.Starting;
    }

    /// <summary>True while the cancel gesture is disarmed; clears the stamp once the window has passed.</summary>
    private bool IsWithinCancelGuardWindow()
    {
        if (_transcriptionStartedAt is not long startedAt) return false;
        if (_nowMs() - startedAt < CancelGuardWindowMs) return true;
        _transcriptionStartedAt = null;
        return false;
    }
}
