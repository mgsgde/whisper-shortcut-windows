using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class LiveMeetingSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-session-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingStore _store;

    // 2 s max, 1 s min, 0.3 s of silence, 0.1 s meter: chunks of ~2 s keep the tests fast.
    private static readonly MeetingChunkPolicy Small = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(0.3), -40f, TimeSpan.FromSeconds(0.1));

    public LiveMeetingSessionTests() => _store = new MeetingStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private LiveMeetingSession NewSession(Func<byte[], CancellationToken, Task<string>> transcribe)
    {
        var s = new LiveMeetingSession(_store, transcribe, "mic", Small) { RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero } };
        return s;
    }

    private static void Feed(LiveMeetingSession s, short[] audio, int block = 800)
    {
        for (var i = 0; i < audio.Length; i += block)
            s.WriteAudio(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    /// <summary>Text that tells which chunk it was: the chunk's duration in 0.1 s steps is not stable, so use a counter.</summary>
    private static Func<byte[], CancellationToken, Task<string>> Counting(List<string> texts, Func<int, bool>? fails = null)
    {
        var n = 0;
        return (wav, ct) =>
        {
            var i = Interlocked.Increment(ref n);
            if (fails != null && fails(i)) throw new HttpRequestException("offline");
            var text = "Text " + i;
            lock (texts) texts.Add(text);
            return Task.FromResult(text);
        };
    }

    [Fact]
    public async Task Chunks_are_transcribed_in_order_into_the_transcript()
    {
        var texts = new List<string>();
        using var s = NewSession(Counting(texts));
        s.Start();
        Feed(s, Synth.Speech(5)); // 2 s + 2 s completed, 1 s left
        var result = await s.StopAsync(null, CancellationToken.None);

        Assert.Equal(3, result.TranscribedChunks);
        Assert.Equal(0, result.PendingChunks);
        var chunks = _store.ReadChunks(s.Meeting.Id);
        Assert.Equal(new[] { "Text 1", "Text 2", "Text 3" }, chunks.Select(c => c.Text));
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, chunks.Select(c => c.StartSeconds));
        Assert.Equal(MeetingStatus.Ended, result.Meeting.Status);
        Assert.Equal(5.0, result.Meeting.DurationSeconds, 2);
        Assert.False(s.IsRunning);
        Assert.Equal(3, s.TranscribedChunks);
        Assert.Empty(_store.ListPendingAudio(s.Meeting.Id));
        Assert.False(Directory.Exists(Path.Combine(s.Meeting.Folder, "audio-pending")));
    }

    [Fact]
    public async Task StopAsync_returns_a_result_when_meeting_json_is_gone()
    {
        var texts = new List<string>();
        using var s = NewSession(Counting(texts));
        s.Start();
        Feed(s, Synth.Speech(2.5));
        File.Delete(Path.Combine(s.Meeting.Folder, "meeting.json"));
        var result = await s.StopAsync(null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void Meeting_throws_before_Start()
    {
        using var s = NewSession((_, _) => Task.FromResult("x"));
        Assert.Throws<InvalidOperationException>(() => s.Meeting);
        Assert.False(s.IsRunning);
    }

    [Fact]
    public async Task A_delegate_failing_twice_then_succeeding_leaves_no_pending_audio()
    {
        var texts = new List<string>();
        using var s = NewSession(Counting(texts, i => i <= 2));
        s.Start();
        Feed(s, Synth.Speech(1.5));
        var result = await s.StopAsync(null, CancellationToken.None);
        Assert.Equal(1, result.TranscribedChunks);
        Assert.Equal(0, result.PendingChunks);
        Assert.Equal(new[] { "Text 3" }, _store.ReadChunks(s.Meeting.Id).Select(c => c.Text));
        Assert.Empty(_store.ListPendingAudio(s.Meeting.Id));
        Assert.Null(s.LastError);
    }

    [Fact]
    public async Task Always_failing_keeps_the_audio_and_raises_ChunkFailed()
    {
        var failed = 0;
        using var s = NewSession((_, _) => throw new HttpRequestException("offline"));
        s.ChunkFailed += _ => Interlocked.Increment(ref failed);
        s.Start();
        Feed(s, Synth.Speech(4.5)); // two full chunks, 0.5 s left
        await WaitUntil(() => s.PendingChunks == 2);
        var result = await s.StopAsync(null, CancellationToken.None);

        Assert.Equal(3, result.PendingChunks);
        Assert.Equal(0, result.TranscribedChunks);
        Assert.Equal(3, _store.ListPendingAudio(s.Meeting.Id).Count);
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, _store.ListPendingAudio(s.Meeting.Id).Select(p => p.StartSeconds));
        Assert.True(failed >= 2);
        Assert.IsType<HttpRequestException>(s.LastError);
        Assert.Equal(MeetingStatus.Ended, result.Meeting.Status);
    }

    [Fact]
    public async Task A_later_success_splices_the_earlier_failed_chunk_in_at_its_timestamp()
    {
        var calls = 0;
        // Chunk 1 fails all four attempts, then everything works.
        using var s = NewSession((wav, ct) =>
        {
            var i = Interlocked.Increment(ref calls);
            if (i <= 4) throw new HttpRequestException("offline");
            return Task.FromResult("Text " + i);
        });
        s.Start();
        Feed(s, Synth.Speech(4.2)); // chunks at 0 and 2 completed, 0.2 s left (dropped by Flush)
        await WaitUntil(() => s.TranscribedChunks >= 2 && s.QueuedChunks == 0);

        Assert.Equal(0, s.PendingChunks);
        var chunks = _store.ReadChunks(s.Meeting.Id);
        Assert.Equal(new[] { 0.0, 2.0 }, chunks.Select(c => c.StartSeconds));
        Assert.Equal(1, _store.Get(s.Meeting.Id)!.TranscriptRevision); // the early chunk was spliced in
        Assert.Empty(_store.ListPendingAudio(s.Meeting.Id));
        await s.StopAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task Silent_chunks_never_call_the_delegate()
    {
        var calls = 0;
        using var s = NewSession((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("x"); });
        s.Start();
        Feed(s, Synth.Silence(5));
        var result = await s.StopAsync(null, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(0, result.TranscribedChunks);
        Assert.Empty(_store.ReadChunks(s.Meeting.Id));
        Assert.False(Directory.Exists(Path.Combine(s.Meeting.Folder, "audio-pending")));
    }

    [Fact]
    public async Task Empty_text_is_not_added_and_removes_the_audio()
    {
        using var s = NewSession((_, _) => Task.FromResult("   "));
        s.Start();
        Feed(s, Synth.Speech(1.5));
        var result = await s.StopAsync(null, CancellationToken.None);
        Assert.Equal(0, result.TranscribedChunks);
        Assert.Equal(0, result.PendingChunks);
        Assert.Empty(_store.ReadChunks(s.Meeting.Id));
    }

    [Fact]
    public async Task Stop_saves_a_summary_from_the_transcript_without_header()
    {
        string? seen = null;
        using var s = NewSession((_, _) => Task.FromResult("Wir starten"));
        s.Start();
        Feed(s, Synth.Speech(1.5));
        var result = await s.StopAsync((text, ct) => { seen = text; return Task.FromResult("  ## Punkte\n- Start \n"); }, CancellationToken.None);
        Assert.True(result.SummarySaved);
        Assert.Null(result.SummaryError);
        Assert.StartsWith("[00:00] Wir starten", seen);
        Assert.DoesNotContain("# Meeting", seen);
        Assert.Equal("## Punkte\n- Start", _store.ReadSummary(s.Meeting.Id));
        Assert.True(result.Meeting.HasSummary);
    }

    [Fact]
    public async Task A_throwing_summarizer_is_reported_and_the_meeting_still_ends()
    {
        using var s = NewSession((_, _) => Task.FromResult("Text"));
        s.Start();
        Feed(s, Synth.Speech(1.5));
        var result = await s.StopAsync((_, _) => throw new InvalidOperationException("quota"), CancellationToken.None);
        Assert.False(result.SummarySaved);
        Assert.IsType<InvalidOperationException>(result.SummaryError);
        Assert.Equal(MeetingStatus.Ended, _store.Get(s.Meeting.Id)!.Status);
        Assert.Null(_store.ReadSummary(s.Meeting.Id));
    }

    [Fact]
    public async Task No_summary_is_requested_for_an_empty_transcript()
    {
        var asked = false;
        using var s = NewSession((_, _) => Task.FromResult("x"));
        s.Start();
        Feed(s, Synth.Silence(3));
        var result = await s.StopAsync((_, _) => { asked = true; return Task.FromResult("s"); }, CancellationToken.None);
        Assert.False(asked);
        Assert.False(result.SummarySaved);
    }

    [Fact]
    public async Task Dispose_without_Stop_keeps_the_meeting_recording_with_its_audio_and_it_can_be_recovered()
    {
        string id;
        using (var s = NewSession((_, _) => throw new HttpRequestException("offline")))
        {
            s.Start();
            id = s.Meeting.Id;
            Feed(s, Synth.Speech(2.5)); // chunk 0-2 s completes (kept), 0.5 s sits in the partial
            await WaitUntil(() => s.PendingChunks == 1);
        }

        var info = _store.Get(id)!;
        Assert.Equal(MeetingStatus.Recording, info.Status);
        Assert.Equal(1, info.PendingAudioChunks);
        Assert.Single(Directory.GetFiles(Path.Combine(info.Folder, "audio-pending"), "*.pcm"));

        var changed = _store.RecoverInterrupted();
        Assert.Equal(id, Assert.Single(changed).Id);
        Assert.Equal(MeetingStatus.Interrupted, _store.Get(id)!.Status);
        Assert.Equal(new[] { 0.0, 2.0 }, _store.ListPendingAudio(id).Select(p => p.StartSeconds));
        Assert.Equal(2, _store.Get(id)!.PendingAudioChunks);
    }

    [Fact]
    public async Task RetryPendingAsync_recovers_kept_chunks_and_leaves_failures()
    {
        var m = _store.Create(DateTimeOffset.Now, "mic");
        _store.AppendChunk(m.Id, 4, "spaeter");
        _store.KeepPendingAudio(m.Id, 0, Wav.Encode16(Synth.Speech(1), 16000));
        _store.KeepPendingAudio(m.Id, 2, Wav.Encode16(Synth.Speech(1), 16000));
        var n = 0;
        var result = await LiveMeetingSession.RetryPendingAsync(_store, m.Id, (_, _) =>
        {
            if (Interlocked.Increment(ref n) == 2) throw new HttpRequestException("offline");
            return Task.FromResult("frueh");
        }, CancellationToken.None);

        Assert.Equal(1, result.Recovered);
        Assert.Equal(1, result.StillPending);
        Assert.IsType<HttpRequestException>(result.FirstError);
        Assert.Equal(new[] { 2.0 }, _store.ListPendingAudio(m.Id).Select(p => p.StartSeconds));
        Assert.Equal(new[] { "frueh", "spaeter" }, _store.ReadChunks(m.Id).Select(c => c.Text));

        var again = await LiveMeetingSession.RetryPendingAsync(_store, m.Id, (_, _) => Task.FromResult(""), CancellationToken.None);
        Assert.Equal(1, again.Recovered);
        Assert.Equal(0, again.StillPending);
        Assert.Empty(_store.ListPendingAudio(m.Id));
    }

    [Fact]
    public async Task Cancelling_the_stop_wait_leaves_unfinished_chunks_as_pending_audio()
    {
        var gate = new TaskCompletionSource<string>();
        using var s = NewSession((_, ct) => gate.Task.WaitAsync(ct));
        s.Start();
        Feed(s, Synth.Speech(1.5));
        using var cts = new CancellationTokenSource(50);
        var result = await s.StopAsync(null, cts.Token);
        Assert.Equal(0, result.TranscribedChunks);
        Assert.Equal(1, result.PendingChunks);
        Assert.Equal(MeetingStatus.Ended, result.Meeting.Status);
        Assert.Single(_store.ListPendingAudio(s.Meeting.Id));
    }
}
