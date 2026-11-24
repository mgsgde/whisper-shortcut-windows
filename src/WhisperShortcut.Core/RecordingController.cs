namespace WhisperShortcut.Core;

public enum TakePhase { Recording, Processing }

/// <summary>What <see cref="RecordingController{T}"/> needs from a take: its phase, its serial and the request to cancel.</summary>
public interface IRecordingTake : IDisposable
{
    TakePhase Phase { get; set; }

    /// <summary>Bumped by every recording that starts (right Ctrl remembers the one it started).</summary>
    long Serial { get; set; }

    /// <summary>The request of the processing phase; cancelling it is "cancel".</summary>
    CancellationTokenSource? Cts { get; }
}

/// <summary>What <see cref="RecordingController{T}.Cancel"/> did.</summary>
public enum CancelOutcome
{
    /// <summary>Idle: nothing to cancel.</summary>
    None,

    /// <summary>Processing: the request was cancelled; the run ends itself.</summary>
    RequestCancelled,

    /// <summary>Recording: the take was torn down and the controller is idle.</summary>
    RecordingDiscarded,
}

/// <summary>
/// The owner of the recording state: the run in progress (none = idle), the serial counter and the right-Ctrl serial.
/// Idle → Recording (<see cref="Start"/>) → Processing (<see cref="StopAsync"/>, <see cref="BeginProcessing"/>) → Idle
/// (<see cref="End"/>); a replay of kept audio enters Processing from idle (<see cref="Attach"/>). UI thread only.
/// </summary>
public sealed class RecordingController<T> where T : class, IRecordingTake
{
    private long _serial;

    /// <summary>The run from the hotkey press to the end of its processing; null = idle.</summary>
    public T? Current { get; private set; }

    /// <summary>Raised after every phase change (start, processing begins, run ended) unless the caller passed raise: false.</summary>
    public event Action? PhaseChanged;

    /// <summary>The <see cref="IRecordingTake.Serial"/> right Ctrl started, or -1.</summary>
    public long RightCtrlSerial { get; set; } = -1;

    public bool IsIdle => Current == null;
    public bool IsRecording => Current is { Phase: TakePhase.Recording };
    public bool IsProcessing => Current is { Phase: TakePhase.Processing };
    public string PhaseName => Current?.Phase.ToString() ?? "Idle";

    /// <summary>The take that is still recording: a late event of an earlier take must not stop the next one.</summary>
    public bool IsRunning(T take) => ReferenceEquals(Current, take) && take.Phase == TakePhase.Recording;

    /// <summary>Makes <paramref name="take"/> the running recording. False (nothing touched) when a run is already going.</summary>
    public bool Start(T take, bool raise = true)
    {
        if (Current != null) return false;
        take.Phase = TakePhase.Recording;
        take.Serial = ++_serial;
        Current = take;
        if (raise) PhaseChanged?.Invoke();
        return true;
    }

    /// <summary>A take that runs kept audio from idle: it starts in Processing, so there is no Recording phase to announce.</summary>
    public bool Attach(T replay, bool raise = true)
    {
        if (Current != null) return false;
        replay.Phase = TakePhase.Processing;
        Current = replay;
        if (raise) PhaseChanged?.Invoke();
        return true;
    }

    /// <summary>Recording → Processing, once. A repeated call (or one for a take that is not the current run) does nothing.</summary>
    public bool BeginProcessing(T take, bool raise = true)
    {
        if (!ReferenceEquals(Current, take)) return false;
        bool changed = take.Phase != TakePhase.Processing;
        take.Phase = TakePhase.Processing;
        if (changed && raise) PhaseChanged?.Invoke();
        return changed;
    }

    /// <summary>
    /// Stops the recording and hands it to <paramref name="process"/>. The take is Processing before <paramref name="process"/>
    /// runs, so a second stop (a double press) finds nothing to stop. Not recording: completed task, nothing runs.
    /// </summary>
    public Task StopAsync(Func<T, Task> process)
    {
        if (Current is not { Phase: TakePhase.Recording } take) return Task.CompletedTask;
        BeginProcessing(take);
        return process(take);
    }

    /// <summary>Processing: cancels the request. Recording: tears the take down and goes idle. Idle: nothing.</summary>
    public CancelOutcome Cancel(bool raise = true)
    {
        if (Current is not { } take) return CancelOutcome.None;
        if (take.Phase == TakePhase.Processing)
        {
            take.Cts?.Cancel();
            return CancelOutcome.RequestCancelled;
        }
        End(take, raise);
        return CancelOutcome.RecordingDiscarded;
    }

    /// <summary>Cancels the request of a processing run (a hotkey press during processing); nothing else.</summary>
    public void CancelProcessing() => Current?.Cts?.Cancel();

    /// <summary>Tears the run down (<see cref="IDisposable.Dispose"/>) and leaves idle when it is still the current run.</summary>
    public void End(T take, bool raise = true)
    {
        take.Dispose();
        if (!ReferenceEquals(Current, take)) return;
        Current = null;
        if (raise) PhaseChanged?.Invoke();
    }
}
