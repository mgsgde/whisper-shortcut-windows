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
    /// Off only while the settings window is in front and nothing runs, so its hotkey field can record
    /// the very key combination that would otherwise start a dictation.
    /// </summary>
    public static bool WantGlobalHotkeys(bool settingsInFront, bool idle) => !(settingsInFront && idle);
}
