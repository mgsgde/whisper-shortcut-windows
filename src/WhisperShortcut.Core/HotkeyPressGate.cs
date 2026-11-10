namespace WhisperShortcut.Core;

/// <summary>
/// Ignores a hotkey press that cannot be a deliberate stop or cancel: one that arrives while the key
/// of the press that changed the state is still down, or within 300 ms of that press. Key bounce, a
/// KVM or a remote session re-sending keys must not throw away a long dictation. The caller feeds it
/// the clock and the key state, so it needs no keyboard.
/// </summary>
public sealed class HotkeyPressGate
{
    /// <summary>Shortest time between a state change and a press that may act on it.</summary>
    public const long MinGapMs = 300;

    private long _armedAt;
    private bool _armed;
    private bool _released = true;

    /// <summary>True once the key of the arming press was seen up again.</summary>
    public bool Released => _released;

    /// <summary>A hotkey press just changed the state (start or stop of a recording): its key is down until <see cref="KeyUp"/>.</summary>
    public void Armed(long nowMs)
    {
        _armed = true;
        _armedAt = nowMs;
        _released = false;
    }

    /// <summary>The key of the arming press is up.</summary>
    public void KeyUp() => _released = true;

    /// <summary>Whether a press at <paramref name="nowMs"/> may act. Always true when nothing armed the gate.</summary>
    public bool Accept(long nowMs) => !_armed || (_released && nowMs - _armedAt >= MinGapMs);
}

/// <summary>
/// Whether the global hotkeys should be registered right now. Pure so the order of window events
/// (activate, deactivate, close) cannot decide it: the caller re-evaluates after each of them.
/// </summary>
public static class HotkeyPolicy
{
    /// <summary>
    /// Off only while a hotkey field in the settings window has the focus and nothing runs: it records the very
    /// key combination that would otherwise start a dictation (like the Mac's shortcut recorder). Everywhere
    /// else in the settings window the shortcuts keep working, so the user can dictate into its text fields.
    /// </summary>
    public static bool WantGlobalHotkeys(bool recordingHotkeyField, bool idle) => !(recordingHotkeyField && idle);
}

/// <summary>What a hotkey press should do in the recording state machine.</summary>
public enum RecordingHotkeyDecision
{
    /// <summary>Idle: start a recording in this mode.</summary>
    Start,
    /// <summary>Recording in the same mode: stop and process.</summary>
    Stop,
    /// <summary>Processing: cancel the request.</summary>
    Cancel,
    /// <summary>Bounce, held key, or the other mode while a recording already runs.</summary>
    Ignore,
}

/// <summary>
/// Concurrent or bounced hotkeys. The other mode must not stop a recording that just started
/// (Ctrl+Shift+D and Ctrl+Shift+P share modifiers and Windows can fire both). A press the
/// bounce gate rejects must not cancel processing.
/// </summary>
public static class RecordingHotkeyPolicy
{
    /// <summary>
    /// Idle always starts (the bounce gate does not apply). Recording stops only in the same mode
    /// and only when <paramref name="gateOpen"/>. Processing cancels when the gate is open.
    /// </summary>
    public static RecordingHotkeyDecision Decide(bool recording, bool processing, bool sameMode, bool gateOpen)
    {
        if (processing) return gateOpen ? RecordingHotkeyDecision.Cancel : RecordingHotkeyDecision.Ignore;
        if (recording)
        {
            if (!sameMode) return RecordingHotkeyDecision.Ignore;
            return gateOpen ? RecordingHotkeyDecision.Stop : RecordingHotkeyDecision.Ignore;
        }
        return RecordingHotkeyDecision.Start;
    }
}
