namespace WhisperShortcut.Core;

/// <summary>
/// When to rebuild the system-audio loopback after the default output changes or the capture dies.
/// The delay table matches the Mac (<c>rebuildRetryDelay</c>: 0.5 / 1 / 2 s, then stop). A spent
/// budget is not a latch: the next device change starts a fresh one. A capture that dies inside
/// <see cref="StartupGraceSeconds"/> of its start counts as a failed start, so a headset that
/// drops the endpoint immediately cannot spin a new budget forever.
/// </summary>
public static class SystemAudioRebuild
{
    /// <summary>Wait before the first rebuild of a device-change burst, so two notifications become one.</summary>
    public const double DeviceChangeDebounceSeconds = 1;

    /// <summary>A stop this soon after start is a failed start, not a capture that had been running.</summary>
    public const double StartupGraceSeconds = 2;

    /// <summary>One try plus three retries.</summary>
    public const int MaxAttempts = 4;

    /// <summary>Failures so far, whether a rebuild is already scheduled, and whether the budget is used up.</summary>
    public readonly record struct State(int Failures, bool InFlight, bool BudgetSpent);

    /// <summary>Nothing pending.</summary>
    public static State Idle => new(0, false, false);

    /// <summary>
    /// Seconds before retry number <paramref name="failuresSoFar"/> + 1 (0-based failures).
    /// Null once the three retries are used up. Negative counts as used up.
    /// </summary>
    public static double? RetryDelaySeconds(int failuresSoFar)
    {
        if (failuresSoFar < 0) return null;
        double[] delays = { 0.5, 1, 2 };
        return failuresSoFar < delays.Length ? delays[failuresSoFar] : null;
    }

    /// <summary>A default-output change. Always a fresh budget, including after a give-up.</summary>
    public static (State State, double? WaitSeconds) DeviceChanged(State state) =>
        (new State(0, true, false), DeviceChangeDebounceSeconds);

    /// <summary>
    /// The scheduled attempt finished. <paramref name="started"/> is false when opening the new
    /// endpoint threw or the capture already died before the attempt returned.
    /// </summary>
    public static (State State, double? WaitSeconds) AttemptFinished(State state, bool started)
    {
        if (!state.InFlight) return (state, null);
        if (started) return (new State(state.Failures, false, false), null);
        return Failed(state);
    }

    /// <summary>
    /// The running loopback stopped with an error. Ignored while a rebuild is already scheduled and
    /// after the budget is spent (the next device change tries again). A stop after the grace period
    /// is a new episode.
    /// </summary>
    public static (State State, double? WaitSeconds) CaptureStopped(State state, double captureAgeSeconds)
    {
        if (state.BudgetSpent || state.InFlight) return (state, null);
        if (captureAgeSeconds >= StartupGraceSeconds)
            return (new State(0, true, false), DeviceChangeDebounceSeconds);
        return Failed(state);
    }

    private static (State State, double? WaitSeconds) Failed(State state)
    {
        var delay = RetryDelaySeconds(state.Failures);
        var failures = state.Failures + 1;
        if (delay == null) return (new State(failures, false, true), null);
        return (new State(failures, true, false), delay);
    }
}
