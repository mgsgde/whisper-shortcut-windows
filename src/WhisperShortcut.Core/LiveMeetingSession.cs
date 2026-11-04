using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>Outcome of <see cref="LiveMeetingSession.StopAsync"/>.</summary>
/// <param name="Meeting">The meeting as stored after stopping.</param>
/// <param name="TranscribedChunks">Chunks whose text is in the transcript.</param>
/// <param name="PendingChunks">Chunks whose audio is kept because transcription never succeeded.</param>
/// <param name="SummarySaved">summary.md was written.</param>
/// <param name="SummaryError">Why the summary failed, if it did; never thrown.</param>
public sealed record MeetingStopResult(MeetingInfo Meeting, int TranscribedChunks, int PendingChunks, bool SummarySaved, Exception? SummaryError);

/// <summary>Outcome of <see cref="LiveMeetingSession.RetryPendingAsync"/>.</summary>
/// <param name="Recovered">Chunks that went through (including ones with nothing said).</param>
/// <param name="StillPending">Chunks whose audio is still kept.</param>
/// <param name="FirstError">The first failure, or null.</param>
public sealed record MeetingRetryResult(int Recovered, int StillPending, Exception? FirstError);

/// <summary>
/// Runs one Live Meeting: the capture thread feeds audio, the chunker cuts it, one worker transcribes each chunk
/// in order and appends it to the transcript. Audio is never dropped silently: a chunk is written to disk before it
/// is queued, and only removed once its text is stored. No Windows APIs, so it is testable on any OS.
/// </summary>
public sealed class LiveMeetingSession : IDisposable
{
    private static readonly Regex FirstMarker = new(@"^\[\d+:\d{2}\]", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly MeetingStore _store;
    private readonly Func<byte[], CancellationToken, Task<string>> _transcribe;
    private readonly string _audioMode;
    private readonly MeetingChunkPolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _sampleRate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<MeetingAudioChunk> _channel = Channel.CreateUnbounded<MeetingAudioChunk>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _audioGate = new();
    private readonly HashSet<long> _failed = new(); // start in ms of chunks that failed all attempts (worker thread only)

    private MeetingInfo? _meeting;
    private MeetingChunker? _chunker;
    private Task? _worker;
    private FileStream? _partial;
    private double _partialStart;
    private long _fed;
    private int _partialSamples;
    private long _sinceFlush;
    private long _sinceDuration;
    private bool _running;
    private bool _stopped;
    private int _transcribed;
    private int _pending;
    private int _queued;
    private volatile Exception? _lastError;

    /// <summary>
    /// <paramref name="transcribe"/> turns a WAV chunk into cleaned text ("" = nothing said) and throws on failure
    /// (network, quota …). <paramref name="audioMode"/> is stored in meeting.json.
    /// </summary>
    public LiveMeetingSession(MeetingStore store, Func<byte[], CancellationToken, Task<string>> transcribe,
        string audioMode, MeetingChunkPolicy? policy = null, Func<DateTimeOffset>? clock = null, int sampleRate = 16000)
    {
        _store = store;
        _transcribe = transcribe;
        _audioMode = audioMode;
        _policy = policy ?? MeetingChunkPolicy.Default;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _sampleRate = sampleRate;
    }

    /// <summary>Waits between attempts of one chunk: 1 + length attempts in total. Tests set zeros.</summary>
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    /// <summary>The meeting; throws <see cref="InvalidOperationException"/> before <see cref="Start"/>.</summary>
    public MeetingInfo Meeting => _meeting ?? throw new InvalidOperationException("The meeting has not been started.");

    /// <summary>Between <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => _running;

    /// <summary>Audio time fed so far.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)Interlocked.Read(ref _fed) / _sampleRate);

    /// <summary>Chunks whose text is in the transcript (including recovered ones).</summary>
    public int TranscribedChunks => Volatile.Read(ref _transcribed);

    /// <summary>Chunks whose audio is kept after all attempts failed, waiting for a retry.</summary>
    public int PendingChunks => Volatile.Read(ref _pending);

    /// <summary>Chunks that are complete but not yet through transcription.</summary>
    public int QueuedChunks => Volatile.Read(ref _queued);

    /// <summary>The most recent chunk failure.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Any counter changed. Raised on the worker thread, or on the capture thread when a chunk is queued.</summary>
    public event Action? Changed;

    /// <summary>A chunk failed all attempts and its audio is kept. Raised on the worker thread.</summary>
    public event Action<Exception>? ChunkFailed;

    /// <summary>Creates the meeting in the store and starts the single worker.</summary>
    public void Start()
    {
        lock (_audioGate)
        {
            if (_running || _stopped) throw new InvalidOperationException("The session was already started.");
            _meeting = _store.Create(_clock(), _audioMode);
            _chunker = new MeetingChunker(_sampleRate, _policy);
            OpenPartial();
            _running = true;
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
        Log.Info($"Meeting: started ({_audioMode})");
    }

    /// <summary>
    /// Capture thread. Feeds the chunker and the raw partial (flushed at least every second of audio). A completed
    /// silent chunk is discarded without a request; any other is written to disk first (crash safety), then queued.
    /// About every 10 s of audio the stored duration is updated.
    /// </summary>
    public void WriteAudio(ReadOnlySpan<short> samples)
    {
        var queued = false;
        lock (_audioGate)
        {
            if (!_running || _chunker == null || samples.Length == 0) return;
            var chunks = _chunker.Write(samples);
            Interlocked.Add(ref _fed, samples.Length);
            var pos = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var length = (int)Math.Round(chunk.DurationSeconds * _sampleRate);
                if (i == 0)
                {
                    // The first finished chunk began earlier: the partial already holds its start, this span its end.
                    var take = Math.Clamp(length - _partialSamples, 0, samples.Length);
                    WritePartial(samples[..take]);
                    pos = take;
                }
                else
                {
                    // Completed inside this span: the chunk carries its own audio, no partial needed.
                    pos = Math.Min(samples.Length, pos + length);
                }
                queued |= CompleteChunk(chunk);
            }
            if (chunks.Count > 0) OpenPartial();
            WritePartial(samples[pos..]);

            _sinceDuration += samples.Length;
            if (_sinceDuration >= _sampleRate * 10L)
            {
                _sinceDuration = 0;
                try { _store.SetDuration(_meeting!.Id, _chunker.ElapsedSeconds); }
                catch (Exception ex) { Log.Warn($"Meeting: duration not saved ({ex.GetType().Name})"); }
            }
        }
        if (queued) Raise();
    }

    /// <summary>
    /// Stop: flushes the last chunk, waits until the queue is drained (<paramref name="ct"/> cancels the wait; what is
    /// not through stays as pending audio), retries this meeting's pending audio once, marks the meeting ended and,
    /// when <paramref name="summarize"/> is given and the transcript has a chunk, saves the summary. A summary failure
    /// is logged and returned, never thrown.
    /// </summary>
    public async Task<MeetingStopResult> StopAsync(Func<string, CancellationToken, Task<string>>? summarize, CancellationToken ct)
    {
        var meeting = Meeting;
        var id = meeting.Id;
        double elapsed;
        lock (_audioGate)
        {
            if (_stopped) throw new InvalidOperationException("The session was already stopped.");
            _stopped = true;
            _running = false;
            var last = _chunker!.Flush();
            if (last != null)
            {
                CompleteChunk(last);
            }
            else
            {
                ClosePartial();
                _store.DeletePartialAudio(id, _partialStart);
            }
            elapsed = _chunker.ElapsedSeconds;
        }
        _channel.Writer.TryComplete();

        try
        {
            await _worker!.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting: stop the worker, the audio of unfinished chunks is on disk.
            _cts.Cancel();
            try { await _worker!.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        if (!ct.IsCancellationRequested && !_cts.IsCancellationRequested && _store.Get(id)?.PendingAudioChunks > 0)
        {
            var retry = await RetryPendingAsync(_store, id, _transcribe, ct).ConfigureAwait(false);
            Interlocked.Add(ref _transcribed, retry.Recovered);
            Volatile.Write(ref _pending, retry.StillPending);
            if (retry.FirstError != null) _lastError = retry.FirstError;
        }

        try { _store.SetDuration(id, elapsed); }
        catch (Exception ex) { Log.Warn($"Meeting: duration not saved ({ex.GetType().Name})"); }
        try { _store.MarkEnded(id, _clock()); }
        catch (Exception ex) { Log.Warn($"Meeting: end not saved ({ex.GetType().Name})"); }

        var summarySaved = false;
        Exception? summaryError = null;
        if (summarize != null)
        {
            try
            {
                var transcript = _store.ReadTranscript(id) ?? "";
                var first = FirstMarker.Match(transcript);
                if (first.Success && _store.ReadChunks(id).Count > 0)
                {
                    var summary = (await summarize(transcript[first.Index..], ct).ConfigureAwait(false)).Trim();
                    if (summary.Length > 0)
                    {
                        _store.SaveSummary(id, summary);
                        summarySaved = true;
                    }
                }
            }
            catch (Exception ex)
            {
                summaryError = ex;
                Log.Error("Meeting: summary failed", ex);
            }
        }

        var info = _store.Get(id) ?? meeting;
        Log.Info($"Meeting: stopped after {elapsed:0} s, {TranscribedChunks} chunk(s) transcribed, {info.PendingAudioChunks} pending, summary {(summarySaved ? "saved" : "none")}");
        Raise();
        return new MeetingStopResult(info, TranscribedChunks, info.PendingAudioChunks, summarySaved, summaryError);
    }

    /// <summary>
    /// Backlog retry for any meeting (tray item, app start): one attempt per pending chunk, oldest first. Success
    /// splices the text in at the chunk's start and removes the audio; failure keeps it. A silent or empty result
    /// counts as success.
    /// </summary>
    public static Task<MeetingRetryResult> RetryPendingAsync(MeetingStore store, string id,
        Func<byte[], CancellationToken, Task<string>> transcribe, CancellationToken ct)
        => RetryCoreAsync(store, id, transcribe, null, null, ct);

    /// <summary>Cancels the worker. Never deletes audio, so a meeting that was not stopped can be recovered.</summary>
    public void Dispose()
    {
        lock (_audioGate)
        {
            _running = false;
            ClosePartial();
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _channel.Writer.TryComplete();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    // ---------------------------------------------------------------- capture side (under _audioGate)

    private void OpenPartial()
    {
        _partialStart = _chunker!.CurrentChunkStartSeconds;
        _partialSamples = 0;
        _sinceFlush = 0;
        try { _partial = _store.OpenPartialAudio(_meeting!.Id, _partialStart); }
        catch (Exception ex)
        {
            _partial = null;
            Log.Warn($"Meeting: partial audio file not opened ({ex.GetType().Name})");
        }
    }

    private void WritePartial(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return;
        _partialSamples += samples.Length;
        if (_partial == null) return;
        try
        {
            // Windows is little-endian, which is the PCM layout on disk.
            _partial.Write(MemoryMarshal.AsBytes(samples));
            _sinceFlush += samples.Length;
            if (_sinceFlush >= _sampleRate)
            {
                _partial.Flush();
                _sinceFlush = 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Meeting: partial audio write failed ({ex.GetType().Name})");
            ClosePartial();
        }
    }

    private void ClosePartial()
    {
        var partial = _partial;
        _partial = null;
        if (partial == null) return;
        try { partial.Dispose(); } catch (IOException) { }
    }

    /// <summary>Finishes one chunk; true when it was queued for transcription.</summary>
    private bool CompleteChunk(MeetingAudioChunk chunk)
    {
        var id = _meeting!.Id;
        ClosePartial();
        if (chunk.Silent)
        {
            SafeDeletePartial(id, chunk.StartSeconds);
            return false;
        }
        try { _store.KeepPendingAudio(id, chunk.StartSeconds, chunk.Wav); }
        catch (Exception ex) { Log.Error("Meeting: chunk audio could not be saved, transcribing from memory only", ex); }
        SafeDeletePartial(id, chunk.StartSeconds);
        Interlocked.Increment(ref _queued);
        _channel.Writer.TryWrite(chunk);
        return true;
    }

    private void SafeDeletePartial(string id, double start)
    {
        try { _store.DeletePartialAudio(id, start); }
        catch (Exception ex) { Log.Warn($"Meeting: partial audio not deleted ({ex.GetType().Name})"); }
    }

    // ---------------------------------------------------------------- worker

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessAsync(chunk, ct).ConfigureAwait(false);
                Interlocked.Decrement(ref _queued);
                Raise();
            }
        }
        catch (OperationCanceledException)
        {
            // Own cancel (Dispose / abandoned stop): the audio of the chunks not done is still on disk.
        }
    }

    private async Task ProcessAsync(MeetingAudioChunk chunk, CancellationToken ct)
    {
        var id = _meeting!.Id;
        Exception? error = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelays[attempt - 1], ct).ConfigureAwait(false);
            try
            {
                var text = ((await _transcribe(chunk.Wav, ct).ConfigureAwait(false)) ?? "").Trim();
                if (text.Length > 0)
                {
                    _store.AppendChunk(id, chunk.StartSeconds, text);
                    Interlocked.Increment(ref _transcribed);
                }
                _store.RemovePendingAudio(id, chunk.StartSeconds);
                Log.Info($"Meeting: chunk at {chunk.StartSeconds:0} s ({chunk.DurationSeconds:0.#} s audio) done, {text.Length} chars");
                await HealAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex;
                Log.Warn($"Meeting: chunk at {chunk.StartSeconds:0} s attempt {attempt + 1} failed ({ex.GetType().Name})");
            }
        }

        _failed.Add(MillisOf(chunk.StartSeconds));
        Interlocked.Increment(ref _pending);
        _lastError = error;
        Log.Error($"Meeting: chunk at {chunk.StartSeconds:0} s kept as audio after {RetryDelays.Length + 1} attempts", error);
        try { ChunkFailed?.Invoke(error!); }
        catch (Exception ex) { Log.Warn($"Meeting: ChunkFailed handler threw ({ex.GetType().Name})"); }
    }

    /// <summary>After a success: one try for the chunks that failed earlier, so a network hiccup heals during the meeting.</summary>
    private async Task HealAsync(CancellationToken ct)
    {
        if (_failed.Count == 0) return;
        await RetryCoreAsync(_store, _meeting!.Id, _transcribe,
            p => _failed.Contains(MillisOf(p.StartSeconds)),
            (p, hadText) =>
            {
                _failed.Remove(MillisOf(p.StartSeconds));
                Interlocked.Decrement(ref _pending);
                if (hadText) Interlocked.Increment(ref _transcribed);
            }, ct).ConfigureAwait(false);
    }

    private static long MillisOf(double seconds) => (long)Math.Round(seconds * 1000);

    /// <summary>One attempt per pending chunk of a meeting; <paramref name="filter"/> limits which ones.</summary>
    private static async Task<MeetingRetryResult> RetryCoreAsync(MeetingStore store, string id,
        Func<byte[], CancellationToken, Task<string>> transcribe, Func<PendingAudio, bool>? filter,
        Action<PendingAudio, bool>? onRecovered, CancellationToken ct)
    {
        var recovered = 0;
        var still = 0;
        Exception? first = null;
        var list = store.ListPendingAudio(id).Where(p => filter == null || filter(p)).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (ct.IsCancellationRequested)
            {
                still += list.Count - i;
                break;
            }
            try
            {
                var wav = await File.ReadAllBytesAsync(p.Path, ct).ConfigureAwait(false);
                var text = ((await transcribe(wav, ct).ConfigureAwait(false)) ?? "").Trim();
                if (text.Length > 0) store.AppendChunk(id, p.StartSeconds, text);
                store.RemovePendingAudio(id, p.StartSeconds);
                recovered++;
                onRecovered?.Invoke(p, text.Length > 0);
                Log.Info($"Meeting: kept chunk at {p.StartSeconds:0} s recovered, {text.Length} chars");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                still += list.Count - i;
                break;
            }
            catch (Exception ex)
            {
                first ??= ex;
                still++;
                Log.Warn($"Meeting: kept chunk at {p.StartSeconds:0} s still failing ({ex.GetType().Name})");
            }
        }
        return new MeetingRetryResult(recovered, still, first);
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Meeting: Changed handler threw ({ex.GetType().Name})"); }
    }
}
