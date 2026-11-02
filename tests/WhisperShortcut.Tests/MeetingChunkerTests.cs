using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Synthetic PCM for the meeting tests: a sine at amplitude 8000 is "speech", zeros are silence.</summary>
internal static class Synth
{
    public const int Rate = 16000;

    public static short[] Speech(double seconds)
    {
        var s = new short[(int)Math.Round(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        return s;
    }

    public static short[] Silence(double seconds) => new short[(int)Math.Round(seconds * Rate)];

    public static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();
}

public class MeetingChunkerTests
{
    private static List<MeetingAudioChunk> Feed(MeetingChunker c, short[] audio, int block = 800)
    {
        var all = new List<MeetingAudioChunk>();
        for (var i = 0; i < audio.Length; i += block)
            all.AddRange(c.Write(audio.AsSpan(i, Math.Min(block, audio.Length - i))));
        return all;
    }

    [Fact]
    public void Continuous_speech_rotates_at_MaxChunk()
    {
        var c = new MeetingChunker();
        var chunks = Feed(c, Synth.Speech(130));
        Assert.Equal(new[] { 0.0, 60.0 }, chunks.Select(x => x.StartSeconds));
        Assert.All(chunks, x => Assert.Equal(60.0, x.DurationSeconds, 3));
        Assert.Equal(120.0, c.CurrentChunkStartSeconds, 3);
        var last = c.Flush();
        Assert.NotNull(last);
        Assert.Equal(120.0, last!.StartSeconds, 3);
        Assert.Equal(10.0, last.DurationSeconds, 3);
        Assert.Equal(new[] { 0, 1, 2 }, chunks.Append(last).Select(x => x.Index));
    }

    [Fact]
    public void A_pause_after_MinChunk_ends_the_chunk_right_after_the_silence()
    {
        var c = new MeetingChunker();
        var chunks = Feed(c, Synth.Concat(Synth.Speech(12), Synth.Silence(2), Synth.Speech(5)));
        Assert.Single(chunks);
        Assert.InRange(chunks[0].DurationSeconds, 13.5, 13.8);
        Assert.False(chunks[0].Silent);
        Assert.Equal(0.0, chunks[0].StartSeconds);
        Assert.Equal(chunks[0].DurationSeconds, c.CurrentChunkStartSeconds, 3);
    }

    [Fact]
    public void A_pause_before_MinChunk_does_not_rotate_early()
    {
        var c = new MeetingChunker();
        var chunks = Feed(c, Synth.Concat(Synth.Speech(5), Synth.Silence(3), Synth.Speech(1)));
        Assert.Empty(chunks);
        Assert.Equal(0.0, c.CurrentChunkStartSeconds);
    }

    [Fact]
    public void A_chunk_without_any_loud_window_is_silent()
    {
        var c = new MeetingChunker();
        var chunks = Feed(c, Synth.Silence(61));
        // Silence satisfies the pause rule as soon as MinChunk is reached, so these chunks stay short.
        Assert.NotEmpty(chunks);
        Assert.All(chunks, x => Assert.True(x.Silent));
        Assert.All(chunks, x => Assert.InRange(x.DurationSeconds, 10.0, 10.6));
    }

    [Fact]
    public void Start_offset_and_first_index_are_applied()
    {
        var c = new MeetingChunker(policy: null, startOffsetSeconds: 100, firstIndex: 7);
        Assert.Equal(100.0, c.CurrentChunkStartSeconds);
        var chunks = Feed(c, Synth.Speech(61));
        Assert.Equal(100.0, chunks[0].StartSeconds, 3);
        Assert.Equal(7, chunks[0].Index);
        Assert.Equal(160.0, c.CurrentChunkStartSeconds, 3);
        Assert.Equal(161.0, c.ElapsedSeconds, 3);
    }

    [Fact]
    public void Flush_returns_the_rest_and_null_under_half_a_second()
    {
        var c = new MeetingChunker();
        Feed(c, Synth.Speech(0.4));
        Assert.Null(c.Flush());

        var d = new MeetingChunker();
        Feed(d, Synth.Speech(3));
        var chunk = d.Flush();
        Assert.NotNull(chunk);
        Assert.Equal(3.0, chunk!.DurationSeconds, 3);
        Assert.Null(d.Flush());
    }

    [Fact]
    public void Chunk_wavs_decode_and_durations_sum_to_the_input()
    {
        var c = new MeetingChunker();
        var audio = Synth.Concat(Synth.Speech(12), Synth.Silence(2), Synth.Speech(70), Synth.Silence(3), Synth.Speech(4));
        var chunks = Feed(c, audio, block: 1234);
        var last = c.Flush();
        if (last != null) chunks.Add(last);
        double sum = 0;
        foreach (var chunk in chunks)
        {
            var samples = Wav.ReadMonoFloat(chunk.Wav, out var rate);
            Assert.Equal(16000, rate);
            Assert.Equal(chunk.DurationSeconds, samples.Length / 16000.0, 3);
            Assert.Equal(sum, chunk.StartSeconds, 3);
            sum += chunk.DurationSeconds;
        }
        Assert.Equal(audio.Length / 16000.0, sum, 3);
    }

    [Fact]
    public void Small_policy_works_with_odd_write_sizes()
    {
        var policy = new MeetingChunkPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(0.3), -40f, TimeSpan.FromSeconds(0.1));
        var c = new MeetingChunker(policy: policy);
        var chunks = Feed(c, Synth.Speech(5), block: 333);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, x => Assert.Equal(2.0, x.DurationSeconds, 3));
    }
}
