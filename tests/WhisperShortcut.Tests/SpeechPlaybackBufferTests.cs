using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SpeechPlaybackBufferTests
{
    private const int Rate = SpeechPlaybackBuffer.SampleRate;

    private static byte[] Pcm(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)(samples[i] & 0xFF);
            bytes[2 * i + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    private static short[] Sine(double hz, double seconds, double amplitude = 0.5)
    {
        var n = (int)(seconds * Rate);
        var s = new short[n];
        for (var i = 0; i < n; i++) s[i] = (short)(Math.Sin(2 * Math.PI * hz * i / Rate) * amplitude * 32767);
        return s;
    }

    private static SpeechPlaybackBuffer Filled(short[] samples, double speed = 1.0, bool complete = true)
    {
        var b = new SpeechPlaybackBuffer { Speed = speed };
        b.Append(Pcm(samples));
        if (complete) b.MarkComplete();
        return b;
    }

    private static float[] ReadAll(SpeechPlaybackBuffer b, int block = 1024)
    {
        var all = new List<float>();
        var buf = new float[block];
        for (var guard = 0; guard < 100_000; guard++)
        {
            var n = b.Read(buf, 0, block);
            all.AddRange(buf.Take(n));
            if (n < block) break;
        }
        return all.ToArray();
    }

    private static int ZeroCrossings(float[] x, int from, int to)
    {
        var c = 0;
        for (var i = from + 1; i < to; i++) if ((x[i - 1] < 0) != (x[i] < 0)) c++;
        return c;
    }

    [Fact]
    public void Speed1_ReturnsTheExactInputSamples()
    {
        var input = Sine(300, 0.5);
        var b = Filled(input);
        var output = ReadAll(b);
        Assert.Equal(input.Length, output.Length);
        for (var i = 0; i < input.Length; i++) Assert.Equal(input[i] / 32768f, output[i]);
        Assert.True(b.IsFinished);
    }

    [Fact]
    public void Read_HonoursOffset_AndBlockBoundaries()
    {
        var input = Sine(300, 0.1);
        var b = Filled(input);
        var buf = new float[100];
        Array.Fill(buf, 9f);
        Assert.Equal(40, b.Read(buf, 10, 40));
        Assert.Equal(9f, buf[9]);
        Assert.Equal(input[0] / 32768f, buf[10]);
        Assert.Equal(input[39] / 32768f, buf[49]);
        Assert.Equal(9f, buf[50]);
        Assert.Equal(input[40] / 32768f, ReadOne(b));
    }

    private static float ReadOne(SpeechPlaybackBuffer b)
    {
        var one = new float[1];
        Assert.Equal(1, b.Read(one, 0, 1));
        return one[0];
    }

    [Fact]
    public void OddTrailingByte_IsKeptForTheNextAppend()
    {
        var b = new SpeechPlaybackBuffer();
        var bytes = Pcm(new short[] { 1000, -2000, 3000 });
        b.Append(bytes[..3]);
        Assert.Equal(1.0 / Rate, b.BufferedSeconds);
        b.Append(bytes[3..]);
        Assert.Equal(3.0 / Rate, b.BufferedSeconds, 9);
        b.MarkComplete();
        var output = ReadAll(b);
        Assert.Equal(new[] { 1000 / 32768f, -2000 / 32768f, 3000 / 32768f }, output);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(1.25)]
    [InlineData(0.75)]
    public void OutputLength_FollowsTheSpeed(double speed)
    {
        var input = Sine(220, 4.0);
        var output = ReadAll(Filled(input, speed));
        var expected = input.Length / speed;
        Assert.InRange(output.Length, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void OutputLength_FollowsTheSpeed_ForNoise()
    {
        var rnd = new Random(7);
        var input = new short[Rate * 3];
        for (var i = 0; i < input.Length; i++) input[i] = (short)rnd.Next(-8000, 8000);
        var output = ReadAll(Filled(input, 2.0));
        Assert.InRange(output.Length, input.Length / 2.0 * 0.95, input.Length / 2.0 * 1.05);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(0.75)]
    public void Pitch_IsPreserved(double speed)
    {
        var output = ReadAll(Filled(Sine(440, 4.0), speed));
        // Look at the middle, away from the start and the fade-out.
        var from = Rate / 2;
        var to = output.Length - Rate / 2;
        var seconds = (to - from) / (double)Rate;
        var perSecond = ZeroCrossings(output, from, to) / seconds;
        Assert.InRange(perSecond, 880 * 0.95, 880 * 1.05);
        Assert.True(output.Max(Math.Abs) < 0.7f, "overlap-add must not add gain");
    }

    [Fact]
    public void Silence_StaysSilent()
    {
        var output = ReadAll(Filled(new short[Rate], 1.5));
        Assert.All(output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Seek_ClampsToWhatArrived()
    {
        var b = Filled(Sine(440, 2.0));
        b.Seek(-5);
        Assert.Equal(0, b.PositionSeconds);
        b.Seek(1.25);
        Assert.Equal(1.25, b.PositionSeconds, 6);
        b.Seek(99);
        Assert.Equal(2.0, b.PositionSeconds, 6);
        Assert.Equal(0, b.Read(new float[10], 0, 10));
        Assert.True(b.IsFinished);

        b.Seek(1.0);
        Assert.False(b.IsFinished);
        Assert.Equal(1.0, b.PositionSeconds, 6);
        var rest = ReadAll(b);
        Assert.Equal(Rate, rest.Length);
    }

    [Fact]
    public void SkipBy_Minus10At3Seconds_GoesToZero()
    {
        var b = Filled(Sine(440, 6.0));
        b.Seek(3.0);
        b.SkipBy(-10);
        Assert.Equal(0, b.PositionSeconds);
        b.SkipBy(2.5);
        Assert.Equal(2.5, b.PositionSeconds, 6);
        b.SkipBy(100);
        Assert.Equal(6.0, b.PositionSeconds, 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Position_AdvancesInSourceTime(double speed)
    {
        var b = Filled(Sine(440, 10.0), speed);
        var buf = new float[Rate];   // one second of output
        Assert.Equal(Rate, b.Read(buf, 0, buf.Length));
        Assert.Equal(speed, b.PositionSeconds, 2);
        Assert.Equal(Rate, b.Read(buf, 0, buf.Length));
        Assert.Equal(2 * speed, b.PositionSeconds, 2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void IsStarved_WhenTheAudioHasNotArrived_AndRecoversAfterAppend(double speed)
    {
        var b = new SpeechPlaybackBuffer { Speed = speed };
        Assert.True(b.IsStarved);
        b.Append(Pcm(Sine(440, 0.5)));
        Assert.False(b.IsComplete);

        var all = ReadAll(b, 512);
        Assert.True(all.Length > 0);
        Assert.True(all.Length < Rate);
        Assert.True(b.IsStarved);
        Assert.False(b.IsFinished);

        // Source position must not run past what has arrived.
        Assert.True(b.PositionSeconds <= b.BufferedSeconds + 1e-9);

        b.Append(Pcm(Sine(440, 1.0)));
        Assert.True(ReadAll(b, 512).Length > 0);

        b.MarkComplete();
        Assert.False(b.IsStarved);
        ReadAll(b);
        Assert.True(b.IsFinished);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(0.75)]
    public void IsFinished_AfterCompleteAndReadToTheEnd(double speed)
    {
        var b = Filled(Sine(440, 1.0), speed);
        Assert.False(b.IsFinished);
        ReadAll(b);
        Assert.True(b.IsComplete);
        Assert.True(b.IsFinished);
        Assert.False(b.IsStarved);
        Assert.Equal(0, b.Read(new float[64], 0, 64));
    }

    [Fact]
    public void Streaming_InSmallAppends_GivesTheSameLengthAsAllAtOnce()
    {
        var input = Sine(440, 3.0);
        var whole = ReadAll(Filled(input, 1.5)).Length;

        var b = new SpeechPlaybackBuffer { Speed = 1.5 };
        var read = new List<float>();
        var buf = new float[777];
        for (var i = 0; i < input.Length; i += 4800)
        {
            b.Append(Pcm(input[i..Math.Min(input.Length, i + 4800)]));
            int n;
            while ((n = b.Read(buf, 0, buf.Length)) > 0) read.AddRange(buf.Take(n));
        }
        b.MarkComplete();
        int m;
        while ((m = b.Read(buf, 0, buf.Length)) > 0) read.AddRange(buf.Take(m));
        Assert.True(b.IsFinished);
        Assert.InRange(read.Count, whole * 0.97, whole * 1.03);
    }

    [Fact]
    public void SpeedChange_MidStream_ContinuesFromTheSamePlace()
    {
        var b = Filled(Sine(440, 6.0), 1.0);
        var buf = new float[Rate];
        b.Read(buf, 0, buf.Length);
        Assert.Equal(1.0, b.PositionSeconds, 3);
        b.Speed = 2.0;
        Assert.Equal(1.0, b.PositionSeconds, 3);
        b.Read(buf, 0, buf.Length);
        Assert.Equal(3.0, b.PositionSeconds, 2);
        b.Speed = 1.0;
        Assert.Equal(Rate, b.Read(buf, 0, buf.Length));
        Assert.Equal(4.0, b.PositionSeconds, 2);
    }

    [Fact]
    public void Speed_IsClamped()
    {
        var b = new SpeechPlaybackBuffer { Speed = 9 };
        Assert.Equal(2.5, b.Speed);
        b.Speed = 0.1;
        Assert.Equal(0.5, b.Speed);
    }

    [Fact]
    public void Duration_UsesTheEstimateUntilComplete()
    {
        var b = new SpeechPlaybackBuffer();
        b.Append(Pcm(Sine(440, 2.0)));
        Assert.Equal(2.0, b.DurationSeconds, 6);
        b.EstimatedTotalSeconds = 10;
        Assert.Equal(10, b.DurationSeconds, 6);
        b.EstimatedTotalSeconds = 1;
        Assert.Equal(2.0, b.DurationSeconds, 6);
        b.EstimatedTotalSeconds = 10;
        b.MarkComplete();
        Assert.Equal(2.0, b.DurationSeconds, 6);
    }

    [Fact]
    public async Task ConcurrentAppendReadSeek_DoesNotThrow()
    {
        var b = new SpeechPlaybackBuffer { Speed = 1.5 };
        var chunk = Pcm(Sine(440, 0.1));
        var writer = Task.Run(() => { for (var i = 0; i < 100; i++) b.Append(chunk); b.MarkComplete(); });
        var reader = Task.Run(() =>
        {
            var buf = new float[512];
            for (var i = 0; i < 2000 && !b.IsFinished; i++) { b.Read(buf, 0, buf.Length); _ = b.IsStarved; }
        });
        var ui = Task.Run(() => { for (var i = 0; i < 200; i++) { b.Seek(i * 0.01); b.Speed = i % 2 == 0 ? 1.5 : 1.0; } });
        await Task.WhenAll(writer, reader, ui);
    }
}
