using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MeetingAudioTests
{
    [Fact]
    public void Routing_parse_and_name_round_trip_with_fallbacks()
    {
        foreach (var p in Enum.GetValues<MeetingAudioPreference>())
            Assert.Equal(p, MeetingAudioRouting.Parse(MeetingAudioRouting.Name(p)));
        Assert.Equal("auto", MeetingAudioRouting.Name(MeetingAudioPreference.Automatic));
        Assert.Equal("mic", MeetingAudioRouting.Name(MeetingAudioPreference.MicrophoneOnly));
        Assert.Equal("mix", MeetingAudioRouting.Name(MeetingAudioPreference.MicrophoneAndSystem));
        Assert.Equal(MeetingAudioPreference.Automatic, MeetingAudioRouting.Parse(null));
        Assert.Equal(MeetingAudioPreference.Automatic, MeetingAudioRouting.Parse("  "));
        Assert.Equal(MeetingAudioPreference.Automatic, MeetingAudioRouting.Parse("bogus"));
        Assert.Equal(MeetingAudioPreference.MicrophoneAndSystem, MeetingAudioRouting.Parse("  MIX "));
        Assert.Equal(MeetingAudioPreference.MicrophoneOnly, MeetingAudioRouting.Parse("Mic"));
    }

    [Theory]
    [InlineData(5, true, true)]      // headset: the mic cannot hear the other side
    [InlineData(3, true, true)]      // headphones
    [InlineData(1, true, false)]     // speakers: mixing would record the other side twice
    [InlineData(9, true, false)]     // HDMI display
    [InlineData(null, true, true)]   // unknown form factor
    public void Automatic_mix_depends_on_the_output_device(int? formFactor, bool hasDevice, bool expected) =>
        Assert.Equal(expected, MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.Automatic, hasDevice, formFactor));

    [Fact]
    public void Mix_decision_no_device_and_manual_preferences()
    {
        Assert.False(MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.Automatic, false, null));
        Assert.False(MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.Automatic, false, 5));
        Assert.False(MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.MicrophoneOnly, true, 5));
        Assert.True(MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.MicrophoneAndSystem, true, 1));
        Assert.True(MeetingAudioRouting.MixesSystemAudio(MeetingAudioPreference.MicrophoneAndSystem, false, null));
        Assert.True(MeetingAudioRouting.IsLikelySpeaker(1));
        Assert.True(MeetingAudioRouting.IsLikelySpeaker(9));
        Assert.False(MeetingAudioRouting.IsLikelySpeaker(5));
        Assert.False(MeetingAudioRouting.IsLikelySpeaker(null));
    }

    [Fact]
    public void Labels_and_metadata_names()
    {
        Assert.Equal("Mikrofon + PC-Ton", MeetingAudioRouting.Label(true));
        Assert.Equal("nur Mikrofon", MeetingAudioRouting.Label(false));
        Assert.Equal("mic+system", MeetingAudioRouting.MetadataName(true));
        Assert.Equal("mic", MeetingAudioRouting.MetadataName(false));
    }

    [Fact]
    public void MixInto_adds_clamps_and_pads_a_short_system_span()
    {
        short[] mic = { 16384, 30000, -30000, 100 };
        float[] system = { 0.25f, 0.5f, -0.5f }; // shorter than mic: last sample counts as 0
        MeetingAudioMixer.MixInto(mic, system);
        Assert.Equal((short)Math.Round(0.75f * 32767f), mic[0]);
        Assert.Equal((short)32767, mic[1]);
        Assert.Equal((short)-32767, mic[2]);
        Assert.Equal((short)Math.Round(100 / 32768f * 32767f), mic[3]);
    }

    [Fact]
    public void ToMono_averages_channels_and_ignores_a_partial_frame()
    {
        var mono = MeetingAudioMixer.ToMono(new[] { 1f, 0f, 0.5f, 0.5f, 9f }, 2);
        Assert.Equal(new[] { 0.5f, 0.5f }, mono);
        Assert.Equal(new[] { 1f, 2f }, MeetingAudioMixer.ToMono(new[] { 1f, 2f }, 1));
    }

    [Fact]
    public void Pcm16_and_Float32_decoding()
    {
        var pcm = new byte[] { 0x00, 0x40, 0x00, 0x80 }; // 16384, -32768
        Assert.Equal(new[] { 0.5f, -1f }, MeetingAudioMixer.Pcm16ToFloat(pcm));
        var floats = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(floats, 0);
        BitConverter.GetBytes(-0.75f).CopyTo(floats, 4);
        Assert.Equal(new[] { 0.25f, -0.75f }, MeetingAudioMixer.Float32ToFloat(floats));
    }

    [Fact]
    public void RmsDb_of_silence_and_full_scale()
    {
        Assert.Equal(-160f, MeetingAudioMixer.RmsDb(new short[100]));
        Assert.Equal(-160f, MeetingAudioMixer.RmsDb(ReadOnlySpan<short>.Empty));
        var square = new short[100];
        Array.Fill(square, short.MinValue);
        Assert.InRange(MeetingAudioMixer.RmsDb(square), -0.01f, 0.01f);
        var half = new short[100];
        Array.Fill(half, (short)16384);
        Assert.InRange(MeetingAudioMixer.RmsDb(half), -6.1f, -5.9f);
    }

    [Fact]
    public void Resampler_48k_to_16k_keeps_the_count_across_uneven_calls()
    {
        var r = new StreamResampler(48000, 16000);
        var input = new float[48000];
        for (var i = 0; i < input.Length; i++) input[i] = MathF.Sin(i * 0.01f);
        int[] sizes = { 1, 7, 480, 1000, 3333, 5000, 9999, 12345, 2, 48000 - (1 + 7 + 480 + 1000 + 3333 + 5000 + 9999 + 12345 + 2) };
        var total = 0;
        var pos = 0;
        foreach (var n in sizes)
        {
            total += r.Process(input.AsSpan(pos, n)).Length;
            pos += n;
        }
        Assert.Equal(48000, pos);
        Assert.InRange(total, 16000 - 2, 16000 + 2);
    }

    [Fact]
    public void Resampler_constant_signal_stays_constant_and_equal_rates_copy()
    {
        var r = new StreamResampler(44100, 16000);
        var all = new List<float>();
        for (var k = 0; k < 5; k++) all.AddRange(r.Process(Enumerable.Repeat(0.4f, 1000 + k * 37).ToArray()));
        Assert.NotEmpty(all);
        Assert.All(all, v => Assert.Equal(0.4f, v, 4));

        var same = new StreamResampler(16000, 16000).Process(new[] { 1f, 2f, 3f });
        Assert.Equal(new[] { 1f, 2f, 3f }, same);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamResampler(0, 16000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamResampler(16000, -1));
    }

    [Fact]
    public void SystemAudioBuffer_pads_with_zeros_and_reports_the_count()
    {
        var b = new SystemAudioBuffer();
        b.Push(new[] { 1f, 2f, 3f });
        var dest = new float[5];
        Array.Fill(dest, 9f);
        Assert.Equal(3, b.Take(dest));
        Assert.Equal(new[] { 1f, 2f, 3f, 0f, 0f }, dest);
        Assert.Equal(0, b.Count);
        Assert.Equal(0, b.Take(dest));
        Assert.All(dest, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SystemAudioBuffer_drops_the_oldest_on_overflow_and_clears()
    {
        var b = new SystemAudioBuffer(4);
        b.Push(new[] { 1f, 2f, 3f });
        b.Push(new[] { 4f, 5f, 6f });
        Assert.Equal(4, b.Count);
        var dest = new float[4];
        b.Take(dest);
        Assert.Equal(new[] { 3f, 4f, 5f, 6f }, dest);
        b.Push(new[] { 1f });
        b.Clear();
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void SystemAudioBuffer_survives_concurrent_push_and_take()
    {
        var b = new SystemAudioBuffer(1600);
        var done = false;
        var pusher = Task.Run(() =>
        {
            var chunk = new float[160];
            for (var i = 0; i < 2000; i++) b.Push(chunk);
            done = true;
        });
        var taker = Task.Run(() =>
        {
            var dest = new float[100];
            while (!done) b.Take(dest);
        });
        Task.WaitAll(pusher, taker);
        Assert.InRange(b.Count, 0, 1600);
    }
}
