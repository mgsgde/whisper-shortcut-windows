using System.Buffers.Binary;
using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class WavChunkerTests
{
    [Fact]
    public void ShortWav_ReturnsTheSameInstance()
    {
        var wav = Tone(sampleRate: 16000, channels: 1, seconds: 2, gaps: null);
        var parts = WavChunker.Split(wav);
        Assert.Same(wav, Assert.Single(parts));
    }

    [Fact]
    public void AtSplitAbove_StaysOnePiece_JustOverItSplits()
    {
        var wav = Tone(8000, 1, 2, null);
        var at = WavChunker.Split(wav, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(2));
        Assert.Same(wav, Assert.Single(at));

        var over = WavChunker.Split(wav, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.5));
        Assert.True(over.Count > 1);
        Assert.NotSame(wav, over[0]);
        AssertRoundTrip(wav, over);
    }

    [Fact]
    public void SixMinutes_CutsInsideTheSilentGaps_AndRoundTrips()
    {
        const int rate = 16000;
        var wav = Tone(rate, 1, 360, new[] { (110.0, 0.2), (225.0, 0.2) });
        var parts = WavChunker.Split(wav);
        Assert.True(parts.Count > 1);
        AssertRoundTrip(wav, parts);

        var boundaries = BoundarySeconds(parts, rate);
        Assert.Contains(boundaries, t => t >= 110.0 && t < 110.2);
        Assert.Contains(boundaries, t => t >= 225.0 && t < 225.2);
    }

    [Fact]
    public void Stereo_CutsOnFrameBoundaries_AndRoundTrips()
    {
        const int rate = 16000;
        var wav = Tone(rate, 2, 3, new[] { (0.9, 0.2) });
        var parts = WavChunker.Split(wav, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.2));
        Assert.True(parts.Count > 1);
        AssertRoundTrip(wav, parts);
        Assert.Contains(BoundarySeconds(parts, rate), t => t >= 0.9 && t < 1.1);
        foreach (var part in parts)
            Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(part.AsSpan(22)));
    }

    [Fact]
    public void UnreadableOrNot16Bit_ReturnsTheSameInstance()
    {
        var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        Assert.Same(junk, Assert.Single(WavChunker.Split(junk)));

        var eightBit = Tone(16000, 1, 1, null);
        eightBit[34] = 8;
        Assert.Same(eightBit, Assert.Single(WavChunker.Split(eightBit)));
    }

    [Fact]
    public void TrailingPartialFrame_IsNotDropped()
    {
        var wav = Tone(16000, 1, 2, null);
        var withTail = new byte[wav.Length + 1];
        wav.CopyTo(withTail, 0);
        withTail[^1] = 0xAB;
        BinaryPrimitives.WriteInt32LittleEndian(withTail.AsSpan(4), withTail.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(withTail.AsSpan(40), withTail.Length - 44);

        var parts = WavChunker.Split(withTail, TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1));
        Assert.True(parts.Count > 1);
        var concat = ConcatPcm(parts);
        Assert.Equal(0xAB, concat[^1]);
        Assert.True(withTail.AsSpan(44).SequenceEqual(concat));
    }

    private static void AssertRoundTrip(byte[] wav, IReadOnlyList<byte[]> parts)
    {
        foreach (var part in parts)
        {
            Assert.True(Wav.DurationSeconds(part) > 0);
            var frames = Wav.ReadMonoFloat(part, out var rate);
            Assert.True(rate > 0);
            Assert.NotEmpty(frames);
            AssertCanonicalHeader(part);
        }
        var original = wav.AsSpan(44, wav.Length - 44);
        Assert.True(original.SequenceEqual(ConcatPcm(parts)));
    }

    private static void AssertCanonicalHeader(byte[] wav)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(44 + dataSize, wav.Length);
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
    }

    private static List<double> BoundarySeconds(IReadOnlyList<byte[]> parts, int sampleRate)
    {
        var boundaries = new List<double>();
        double time = 0;
        for (var i = 0; i < parts.Count - 1; i++)
        {
            var frames = Wav.ReadMonoFloat(parts[i], out _);
            time += frames.Length / (double)sampleRate;
            boundaries.Add(time);
        }
        return boundaries;
    }

    private static byte[] ConcatPcm(IReadOnlyList<byte[]> parts)
    {
        var len = 0;
        foreach (var part in parts) len += part.Length - 44;
        var all = new byte[len];
        var offset = 0;
        foreach (var part in parts)
        {
            var n = part.Length - 44;
            part.AsSpan(44, n).CopyTo(all.AsSpan(offset));
            offset += n;
        }
        return all;
    }

    /// <summary>16-bit PCM. Gaps are silent stretches at the given start time, so a pause search can land in them.</summary>
    private static byte[] Tone(int sampleRate, int channels, double seconds, (double At, double Length)[]? gaps)
    {
        var frames = (int)Math.Round(seconds * sampleRate);
        var pcm = new byte[frames * channels * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / sampleRate;
            var silent = false;
            if (gaps != null)
            {
                foreach (var gap in gaps)
                {
                    if (t >= gap.At && t < gap.At + gap.Length) { silent = true; break; }
                }
            }
            // Square wave, not a sine: every non-silent 100 ms window has the same energy,
            // so the unique minimum is the gap and the test does not depend on phase.
            short sample = 0;
            if (!silent) sample = (i / 16) % 2 == 0 ? (short)8000 : (short)-8000;
            for (var c = 0; c < channels; c++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), sample);
        }
        return Wrap(sampleRate, channels, pcm);
    }

    private static byte[] Wrap(int sampleRate, int channels, byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var blockAlign = channels * 2;
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
