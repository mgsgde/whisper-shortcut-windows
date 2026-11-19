using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// The soft max piece length of Dictate Live (Mac #483): after 5 s, two quiet 0.2 s windows under the
/// pause threshold plus 10 dB cut. The 30 s hard cut stays. Pure decisions on synthetic levels.
/// </summary>
public class LiveSoftCutTests
{
    private static LivePieceCut.SilenceCriterion Criterion(MeetingChunkPolicy policy, double seconds, float baseDb = -45f) =>
        LivePieceCut.Criterion(policy, seconds, baseDb);

    private static float[] Levels(params (float Db, double Seconds)[] parts)
    {
        var list = new List<float>();
        foreach (var (db, seconds) in parts)
        {
            int n = (int)Math.Round(seconds / 0.2);
            for (int i = 0; i < n; i++) list.Add(db);
        }
        return list.ToArray();
    }

    [Fact]
    public void No_soft_cut_before_5s()
    {
        var c = Criterion(LiveDictation.ChunkPolicy, 4.8);
        Assert.False(c.IsSoft);
        Assert.Equal(3, c.DecisionsNeeded);
        Assert.Equal(-45f, c.ThresholdDb);
    }

    [Fact]
    public void From_5s_two_decisions_under_threshold_plus_10_dB_cut()
    {
        var c = Criterion(LiveDictation.ChunkPolicy, 5.0);
        Assert.True(c.IsSoft);
        Assert.Equal(2, c.DecisionsNeeded);
        Assert.Equal(-35f, c.ThresholdDb);
    }

    [Fact]
    public void The_relaxed_threshold_is_capped_at_minus_20_dB() =>
        Assert.Equal(-20f, Criterion(LiveDictation.ChunkPolicy, 6, baseDb: -30f).ThresholdDb);

    [Fact]
    public void The_boost_is_applied_to_the_adaptive_threshold() =>
        Assert.Equal(-50f, Criterion(LiveDictation.ChunkPolicy, 6, baseDb: -60f).ThresholdDb);

    [Fact]
    public void Meetings_are_unchanged_at_any_length()
    {
        foreach (var seconds in new[] { 1.0, 5.0, 30.0 })
        {
            var c = Criterion(MeetingChunkPolicy.Default, seconds, baseDb: -40f);
            Assert.False(c.IsSoft);
            Assert.Equal(-40f, c.ThresholdDb);
            Assert.Equal(5, c.DecisionsNeeded); // 1.5 s / 0.3 s windows
        }
    }

    [Fact]
    public void Kill_switch_restores_the_old_live_behavior()
    {
        Assert.False(LiveDictation.SoftCutDisabledValue(null));
        Assert.False(LiveDictation.SoftCutDisabledValue("1"));
        Assert.True(LiveDictation.SoftCutDisabledValue("0"));

        var on = LiveDictation.ChunkPolicyFor(softCutDisabled: false);
        var off = LiveDictation.ChunkPolicyFor(softCutDisabled: true);
        Assert.Equal(TimeSpan.FromSeconds(5), on.SoftCutAfter);
        Assert.Null(off.SoftCutAfter);
        var c = Criterion(off, 12);
        Assert.False(c.IsSoft);
        Assert.Equal(3, c.DecisionsNeeded);
        Assert.Equal(-45f, c.ThresholdDb);
    }

    [Fact]
    public void A_soft_pause_after_5s_cuts_and_the_kill_switch_does_not()
    {
        // Loud, then a murmur under the soft threshold (−35) but above the hard one (−45), then loud again.
        var levels = Levels((-10f, 5.4), (-40f, 0.4), (-10f, 3));
        var soft = LivePieceCut.Cuts(levels, LiveDictation.ChunkPolicy, -45f);
        var old = LivePieceCut.Cuts(levels, LiveDictation.ChunkPolicyFor(true), -45f);
        var cut = Assert.Single(soft);
        Assert.True(cut.Soft);
        Assert.Equal(29, cut.EndWindow); // 5.8 s
        Assert.Empty(old);
    }

    [Fact]
    public void One_quiet_window_after_5s_does_not_cut()
    {
        var levels = Levels((-10f, 5.2), (-40f, 0.2), (-10f, 2));
        Assert.Empty(LivePieceCut.Cuts(levels, LiveDictation.ChunkPolicy, -45f));
    }

    [Fact]
    public void A_real_pause_before_5s_still_needs_three_windows()
    {
        var shortPause = Levels((-10f, 2.0), (-50f, 0.4), (-10f, 1.0));
        Assert.Empty(LivePieceCut.Cuts(shortPause, LiveDictation.ChunkPolicy, -45f));

        var longPause = Levels((-10f, 2.0), (-50f, 0.6), (-10f, 1.0));
        var cut = Assert.Single(LivePieceCut.Cuts(longPause, LiveDictation.ChunkPolicy, -45f));
        Assert.False(cut.Soft);
        Assert.Equal(13, cut.EndWindow); // 2.6 s
    }

    [Fact]
    public void Silence_before_the_minimum_does_not_end_the_piece()
    {
        var levels = Levels((-10f, 0.8), (-80f, 0.6));
        Assert.Empty(LivePieceCut.Cuts(levels, LiveDictation.ChunkPolicy, -45f));
    }

    [Fact]
    public void Thirty_seconds_of_loud_speech_still_hard_cuts()
    {
        var levels = Levels((-10f, 32));
        var cut = Assert.Single(LivePieceCut.Cuts(levels, LiveDictation.ChunkPolicy, -45f));
        Assert.False(cut.Soft);
        Assert.Equal(150, cut.EndWindow); // 30 s
        Assert.Empty(LivePieceCut.Cuts(Levels((-10f, 30)), LiveDictation.ChunkPolicy, -45f));
    }

    [Fact]
    public void The_chunker_cuts_a_soft_pause_and_keeps_the_30s_backstop()
    {
        var soft = Feed(LiveDictation.ChunkPolicy, Tone(5.4, -10), Tone(0.4, -40), Tone(3, -10));
        Assert.Equal(2, soft.Count);
        Assert.True(soft[0].SoftCut);
        Assert.InRange(soft[0].DurationSeconds, 5.7, 5.9);
        Assert.False(soft[1].SoftCut);

        var old = Feed(LiveDictation.ChunkPolicyFor(true), Tone(5.4, -10), Tone(0.4, -40), Tone(3, -10));
        Assert.Single(old);
        Assert.False(old[0].SoftCut);

        var hard = Feed(LiveDictation.ChunkPolicy, Tone(31, -10));
        Assert.Equal(2, hard.Count);
        Assert.False(hard[0].SoftCut);
        Assert.InRange(hard[0].DurationSeconds, 29.9, 30.1);
    }

    private const int Rate = 16000;

    private static short[] Tone(double seconds, double db, double hz = 440)
    {
        var amplitude = Math.Sqrt(2) * 32768 * Math.Pow(10, db / 20);
        var s = new short[(int)Math.Round(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return s;
    }

    private static List<MeetingAudioChunk> Feed(MeetingChunkPolicy policy, params short[][] parts)
    {
        var audio = parts.SelectMany(p => p).ToArray();
        var c = new MeetingChunker(Rate, policy);
        var all = new List<MeetingAudioChunk>();
        for (var i = 0; i < audio.Length; i += 1600)
            all.AddRange(c.Write(audio.AsSpan(i, Math.Min(1600, audio.Length - i))));
        if (c.Flush() is { } tail) all.Add(tail);
        return all;
    }
}
