using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Live diktieren with a quiet microphone (diagnosis 2026-11-05): speech around -52 dBFS over a -75 dB room must not be
/// cut as pauses or dropped as silence. Live meetings keep their fixed threshold.
/// </summary>
public class LiveQuietSpeechTests
{
    private const int Rate = 16000;

    /// <summary>A sine whose RMS level is <paramref name="db"/> dBFS.</summary>
    private static short[] Tone(double seconds, double db, double hz = 440)
    {
        var amplitude = Math.Sqrt(2) * 32768 * Math.Pow(10, db / 20);
        var s = new short[(int)Math.Round(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return s;
    }

    private static short[] Room(double seconds) => Tone(seconds, -75, 97);

    private static short[] QuietSpeech(double seconds) => Tone(seconds, -52);

    private static List<MeetingAudioChunk> FeedAll(MeetingChunker c, short[] audio)
    {
        var all = new List<MeetingAudioChunk>();
        for (var i = 0; i < audio.Length; i += 1600) // 100 ms buffers, like TrayApp.FeedLive
            all.AddRange(c.Write(audio.AsSpan(i, Math.Min(1600, audio.Length - i))));
        if (c.Flush() is { } tail) all.Add(tail);
        return all;
    }

    private static short[] QuietDictation() => Synth.Concat(Room(1), QuietSpeech(2), Room(1), QuietSpeech(2), Room(1));

    // ---- Fix 2: the pause threshold follows the noise floor ----

    [Fact]
    public void Quiet_speech_in_a_quiet_room_is_cut_at_its_pauses_and_never_silent()
    {
        var chunks = FeedAll(new MeetingChunker(Rate, LiveDictation.ChunkPolicy), QuietDictation());
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.False(c.Silent));
        // Each piece holds one whole stretch of speech: cut 0.6 s into the room tone after it, not inside the speech.
        Assert.InRange(chunks[0].DurationSeconds, 3.5, 3.7);
        Assert.InRange(chunks[1].DurationSeconds, 2.9, 3.1);
    }

    [Fact]
    public void The_fixed_threshold_drops_the_same_quiet_speech_as_silence()
    {
        var fixedPolicy = LiveDictation.ChunkPolicy with { NoiseFloorMarginDb = null };
        var chunks = FeedAll(new MeetingChunker(Rate, fixedPolicy), QuietDictation());
        Assert.All(chunks, c => Assert.True(c.Silent));
    }

    [Fact]
    public void The_relative_threshold_is_floor_plus_margin_within_its_bounds()
    {
        var quiet = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        quiet.Write(Tone(2, -50));
        Assert.InRange(quiet.NoiseFloorDb, -51f, -50f); // whole-dB bins
        Assert.Equal(-45f, quiet.SilenceThresholdDb); // floor + 12 is above -45: never stricter than the Mac

        var veryQuiet = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        veryQuiet.Write(Room(2));
        Assert.Equal(-60f, veryQuiet.SilenceThresholdDb); // -75 + 12 = -63, bounded at -60

        var middle = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        middle.Write(Tone(2, -65));
        Assert.InRange(middle.SilenceThresholdDb, -54f, -52f);
    }

    [Fact]
    public void A_noisy_room_keeps_the_fixed_threshold()
    {
        var c = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        c.Write(Tone(3, -40, 97));
        Assert.Equal(-45f, c.SilenceThresholdDb);
    }

    [Fact]
    public void The_threshold_stays_fixed_until_one_second_was_measured()
    {
        var c = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        Assert.Equal(-45f, c.SilenceThresholdDb);
        c.Write(Room(0.8)); // four 0.2 s windows
        Assert.Equal(-45f, c.SilenceThresholdDb);
        c.Write(Room(0.2));
        Assert.Equal(-60f, c.SilenceThresholdDb);
    }

    [Fact]
    public void The_noise_floor_is_a_low_percentile_not_the_speech()
    {
        var c = new MeetingChunker(Rate, LiveDictation.ChunkPolicy);
        // 80 % speech at -30 dB, 20 % room tone: the floor is still the room.
        c.Write(Synth.Concat(Tone(4, -30), Room(1)));
        Assert.InRange(c.NoiseFloorDb, -76f, -74f);
    }

    [Fact]
    public void Live_meetings_keep_their_fixed_threshold()
    {
        Assert.Null(MeetingChunkPolicy.Default.NoiseFloorMarginDb);
        Assert.Null(MeetingOptions.Policy(60).NoiseFloorMarginDb);
        var c = new MeetingChunker(Rate, MeetingChunkPolicy.Default);
        c.Write(Room(5));
        Assert.Equal(-40f, c.SilenceThresholdDb);
    }

    [Fact]
    public void A_chunk_reports_its_loudest_window()
    {
        var chunks = FeedAll(new MeetingChunker(Rate, LiveDictation.ChunkPolicy), QuietDictation());
        Assert.InRange(chunks[0].LoudestWindowDb, -52.5f, -51.5f);
    }

    // ---- Fix 1: a nearly silent piece is decoded, not dropped ----

    [Fact]
    public void A_silent_piece_just_under_the_threshold_is_decoded()
    {
        Assert.True(LiveDictation.ShouldDecode(silent: false, loudestWindowDb: -160f));
        Assert.True(LiveDictation.ShouldDecode(silent: true, loudestWindowDb: -46f));
        Assert.True(LiveDictation.ShouldDecode(silent: true, loudestWindowDb: -55f));
        Assert.False(LiveDictation.ShouldDecode(silent: true, loudestWindowDb: -55.1f));
        Assert.False(LiveDictation.ShouldDecode(silent: true, loudestWindowDb: -70f));
    }

    [Fact]
    public void LoudestWindowDb_reads_the_wav()
    {
        var wav = Wav.Encode16(Synth.Concat(Room(1), Tone(0.4, -48), Room(1)), Rate);
        Assert.InRange(LiveDictation.LoudestWindowDb(wav), -48.5f, -47.5f);
        Assert.Equal(-160f, LiveDictation.LoudestWindowDb(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task The_session_decodes_a_quiet_silent_piece_and_skips_a_really_silent_one()
    {
        var quiet = Wav.Encode16(Synth.Concat(Room(0.5), Tone(1, -50), Room(0.5)), Rate);
        var still = Wav.Encode16(Room(2), Rate);
        var decoded = new List<byte[]>();
        var inserted = new List<string>();
        var s = new LiveDictationSession(
            (wav, _) => { decoded.Add(wav); return Task.FromResult(ReferenceEquals(wav, quiet) ? "Gangbild" : ""); },
            text => { inserted.Add(text); return Task.FromResult(true); });
        s.Add(still, silent: true);
        s.Add(quiet, silent: true);
        var outcome = await s.FinishAsync(Wav.Encode16(Room(3), Rate));
        Assert.Single(decoded);
        Assert.Same(quiet, decoded[0]);
        Assert.Equal("Gangbild", outcome.Text);
    }
}
