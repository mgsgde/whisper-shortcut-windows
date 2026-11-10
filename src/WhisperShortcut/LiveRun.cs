using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>
/// The live-dictation part of a <see cref="Take"/> (macOS Dictate Live): the session that decodes and inserts the
/// pieces, the feed that cuts the capture thread's audio at pauses, the window it types into, the punctuation mode
/// fixed at its start, and the timer that retries held text once that window is in front again. <see cref="Dispose"/>
/// forgets all of it and cancels a session that did not finish; it never touches the clipboard (that is
/// <c>ClipboardPaster.EndLive</c>, called by whoever ends the take).
/// </summary>
internal sealed class LiveRun : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Core.LivePieceFeed _feed = new();
    // Windows equivalent of the Mac's didActivateApplication retry: the dictated-into window is in front again.
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 500 };
    private volatile bool _closed;
    private bool _finished;

    /// <param name="decode">Decodes one piece with this run's punctuation mode.</param>
    /// <param name="insert">Types a piece into the window (the run's <see cref="Target"/> at that moment); false = hold it.</param>
    public LiveRun(SynchronizationContext ui, Core.PunctuationMode punctuation, string? targetApp, Core.CaretContext caret,
        Func<byte[], Core.PunctuationMode, CancellationToken, Task<string>> decode, Func<string, IntPtr, Task<bool>> insert)
    {
        _ui = ui;
        Punctuation = punctuation;
        TargetApp = targetApp;
        Session = new Core.LiveDictationSession(
            (wav, ct) => decode(wav, Punctuation, ct),
            text => insert(text, Target),
            holdsPeriods: punctuation != Core.PunctuationMode.SpokenOnly,
            caret: caret);
        _retryTimer.Tick += (_, _) =>
        {
            if (Session.HasHeldText && ClipboardPaster.ForegroundRoot() == Target) Session.RetryHeld();
        };
    }

    public Core.LiveDictationSession Session { get; }

    /// <summary>The window the live dictation types into; set once the recording runs (zero = pieces are held).</summary>
    public IntPtr Target { get; set; }

    /// <summary>Process the dictation inserts into (Mac liveTargetBundleId), for the last-insertion memory.</summary>
    public string? TargetApp { get; }

    /// <summary>Punctuation mode, fixed at the start like Diktieren (Mac livePunctuationMode).</summary>
    public Core.PunctuationMode Punctuation { get; }

    public void StartRetryTimer() => _retryTimer.Start();

    /// <summary>Capture thread: cuts the stream at pauses and hands finished pieces to the UI thread.</summary>
    public void Feed(byte[] buffer, int count)
    {
        if (_feed.Write(buffer.AsSpan(0, count))) _ui.Post(_ => Drain(), null);
    }

    /// <summary>A test file arrives whole: cut it like a recording would have been.</summary>
    public void FeedWhole(ReadOnlySpan<byte> pcm16) => _feed.Write(pcm16);

    /// <summary>The recording ended: earlier pieces first, then the tail.</summary>
    public void EndRecording()
    {
        var tail = _feed.Finish();
        Drain();
        if (tail != null) Session.Add(tail.Wav, tail.Silent, new Core.LivePieceInfo("Stop", Stopwatch.GetTimestamp()));
    }

    /// <summary>UI thread: hands the finished pieces to the session, in order.</summary>
    private void Drain()
    {
        while (!_closed && _feed.TryTake(out var cut))
        {
            var chunk = cut.Chunk;
            var reason = chunk.DurationSeconds >= Core.LiveDictation.ChunkPolicy.MaxChunk.TotalSeconds - 0.01 ? "Max" : "Pause";
            Session.Add(chunk.Wav, chunk.Silent, new Core.LivePieceInfo(reason, cut.Cut));
        }
    }

    public async Task<Core.LiveDictationOutcome> FinishAsync(byte[] wav)
    {
        var outcome = await Session.FinishAsync(wav);
        _finished = true;
        return outcome;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _retryTimer.Stop();
        _retryTimer.Dispose();
        _feed.Close();
        if (!_finished) Session.Cancel(); // a finished session needs no cancel (and its log line would only mislead)
    }
}
