namespace WhisperShortcut.Core;

/// <summary>
/// The late-copy watch of ONE recording: after a Ctrl+C missed the deadline, the clipboard is watched a little
/// longer. Each recording owns its own object, so another clipboard user (the screenshot hotkey) can never cancel
/// or reset it. Only touched on the UI thread.
/// </summary>
public sealed class LateCopyWatch
{
    private CancellationTokenSource? _cts;

    /// <summary>The clipboard sequence number a change is measured against.</summary>
    public uint Baseline { get; private set; }

    /// <summary>True when a Ctrl+C that missed the deadline arrived afterwards: a selection the recording never saw.</summary>
    public bool Arrived { get; private set; }

    public bool IsActive => _cts != null;

    /// <summary>Starts (or restarts) the watch; the returned token ends when <see cref="Cancel"/> is called.</summary>
    public CancellationToken Start(uint baseline)
    {
        Cancel();
        Baseline = baseline;
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    /// <summary>Our own or a screenshot's clipboard write is no late copy: measure from here on.</summary>
    public void Rebaseline(uint sequence)
    {
        if (_cts != null) Baseline = sequence;
    }

    public void MarkArrived() => Arrived = true;

    /// <summary>The watch ran out (window over, restore done): stops, but keeps <see cref="Arrived"/> for the caller.</summary>
    public void Finish(CancellationToken token)
    {
        if (_cts?.Token != token) return;
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Stops the watch and clears its flag (recording over, Esc, paste, retry).</summary>
    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        Arrived = false;
        if (cts == null) return;
        try { cts.Cancel(); } finally { cts.Dispose(); }
    }
}
