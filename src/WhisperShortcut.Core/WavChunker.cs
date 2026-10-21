using System.Buffers.Binary;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// Splits a long 16-bit PCM recording into pieces a transcription request can carry.
/// Cuts sit in the quietest moment near each boundary so a word is not sliced in half.
/// </summary>
public static class WavChunker
{
    /// <summary>Target length of one piece. The real cut is up to <see cref="SearchWindow"/> earlier, in a pause.</summary>
    public static readonly TimeSpan ChunkLength = TimeSpan.FromSeconds(120);

    /// <summary>How far before the target the quietest 100 ms may be chosen.</summary>
    public static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(20);

    /// <summary>Recordings this long or shorter stay one piece: a cut always risks a split word.</summary>
    public static readonly TimeSpan SplitAbove = TimeSpan.FromSeconds(150);

    /// <summary>Splits <paramref name="wav"/> with <see cref="ChunkLength"/>, <see cref="SearchWindow"/> and <see cref="SplitAbove"/>.</summary>
    public static IReadOnlyList<byte[]> Split(byte[] wav) =>
        Split(wav, ChunkLength, SearchWindow, SplitAbove);

    /// <summary>
    /// Shorter pieces for the on-device recognizer: Parakeet's cost grows faster than the length
    /// (5.5 min on an M1: 24 s in 100 s pieces, 15 s in 30 s pieces), and in a slow VM a 100 s
    /// piece ran for minutes.
    /// </summary>
    public static IReadOnlyList<byte[]> SplitForLocal(byte[] wav) =>
        Split(wav, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(40));

    /// <summary>
    /// Same split with explicit boundaries, so tests can use a short synthetic file.
    /// A recording no longer than <paramref name="splitAbove"/> is returned as the same array:
    /// copying it would only burn memory before a single request.
    /// </summary>
    public static IReadOnlyList<byte[]> Split(byte[] wav, TimeSpan chunkLength, TimeSpan searchWindow, TimeSpan splitAbove)
    {
        if (!TryParse16(wav, out var sampleRate, out var channels, out var dataOffset, out var dataLength))
            return new[] { wav };

        var frameBytes = channels * 2;
        var byteRate = sampleRate * frameBytes;
        if (byteRate <= 0) return new[] { wav };
        // True PCM duration, not the header's byteRate: a wrong rate must not hide a long recording.
        if ((double)dataLength / byteRate <= splitAbove.TotalSeconds) return new[] { wav };

        var chunkFrames = Frames(chunkLength, sampleRate);
        var searchFrames = Frames(searchWindow, sampleRate);
        var windowFrames = Frames(TimeSpan.FromMilliseconds(100), sampleRate);
        var stepFrames = Frames(TimeSpan.FromMilliseconds(10), sampleRate);
        if (windowFrames < 1) windowFrames = 1;
        if (stepFrames < 1) stepFrames = 1;
        if (chunkFrames < 1) return new[] { wav };

        var totalFrames = dataLength / frameBytes;
        var chunks = new List<byte[]>();
        var cursor = 0;
        while (cursor < totalFrames)
        {
            var target = cursor + chunkFrames;
            // Whatever is left does not fill another piece.
            if (target >= totalFrames)
            {
                chunks.Add(Encode(wav, dataOffset + cursor * frameBytes, dataLength - cursor * frameBytes, sampleRate, channels));
                break;
            }

            var cut = QuietCut(wav, dataOffset, frameBytes, channels, cursor, target, searchFrames, windowFrames, stepFrames);
            // A tail shorter than the search window is too small to send alone, and cutting it off
            // would drop the end of the last sentence. Keep it on the previous piece.
            if (cut <= cursor || totalFrames - cut < searchFrames)
            {
                chunks.Add(Encode(wav, dataOffset + cursor * frameBytes, dataLength - cursor * frameBytes, sampleRate, channels));
                break;
            }

            chunks.Add(Encode(wav, dataOffset + cursor * frameBytes, (cut - cursor) * frameBytes, sampleRate, channels));
            cursor = cut;
        }

        return chunks.Count == 0 ? new[] { wav } : chunks;
    }

    /// <summary>Frame index of the middle of the quietest 100 ms window in [target - search, target].</summary>
    private static int QuietCut(byte[] wav, int dataOffset, int frameBytes, int channels,
        int startFrame, int targetFrame, int searchFrames, int windowFrames, int stepFrames)
    {
        var searchStart = targetFrame - searchFrames;
        if (searchStart < startFrame) searchStart = startFrame;
        var cut = targetFrame;
        var best = long.MaxValue;
        var samples = windowFrames * channels;
        for (var w = searchStart; w + windowFrames <= targetFrame; w += stepFrames)
        {
            var sum = SumAbs(wav, dataOffset + w * frameBytes, samples);
            // Earlier window wins a tie, so a long pause is cut at its beginning, not its end.
            if (sum < best)
            {
                best = sum;
                cut = w + windowFrames / 2;
            }
        }
        return cut;
    }

    private static long SumAbs(byte[] wav, int offset, int samples)
    {
        long sum = 0;
        for (var i = 0; i < samples; i++)
            sum += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(offset + i * 2, 2)));
        return sum;
    }

    /// <summary>Whole frames for <paramref name="time"/>. Half-frames round away from zero so 10 ms at 16 kHz is 160, not 159.</summary>
    private static int Frames(TimeSpan time, int sampleRate)
    {
        if (time.Ticks <= 0 || sampleRate <= 0) return 0;
        var frames = (time.Ticks * sampleRate + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;
        return frames > int.MaxValue ? int.MaxValue : (int)frames;
    }

    /// <summary>
    /// A 44-byte PCM header rather than a copy of the source header: each piece must stand alone,
    /// and the recorder's extra chunks (fact, LIST) are not part of the audio.
    /// </summary>
    private static byte[] Encode(byte[] source, int offset, int length, int sampleRate, int channels)
    {
        var pcm = new byte[44 + length];
        var blockAlign = channels * 2;
        Encoding.ASCII.GetBytes("RIFF").CopyTo(pcm, 0);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(4), 36 + length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(pcm, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(pcm, 12);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(32), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(pcm, 36);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(40), length);
        source.AsSpan(offset, length).CopyTo(pcm.AsSpan(44));
        return pcm;
    }

    private static bool TryParse16(byte[] wav, out int sampleRate, out int channels, out int dataOffset, out int dataLength)
    {
        sampleRate = channels = dataOffset = dataLength = 0;
        if (wav.Length < 12) return false;
        var pos = 12;
        var haveFmt = false;
        var haveData = false;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt " && !haveFmt)
            {
                if (body + 16 > wav.Length) return false;
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
                // 1 = PCM, 0xFFFE = extensible (PCM in practice for 16 bit), same rule as Wav.ReadMonoFloat.
                if ((format != 1 && format != 0xFFFE) || bits != 16 || channels < 1 || sampleRate <= 0)
                    return false;
                haveFmt = true;
            }
            else if (id == "data" && !haveData)
            {
                var available = (long)wav.Length - body;
                if (available < 0) return false;
                var length = size > available ? available : size;
                if (length > int.MaxValue) return false;
                dataOffset = body;
                dataLength = (int)length;
                haveData = true;
            }

            var step = (long)size + (size & 1);
            if (step > wav.Length - body) break;
            pos = body + (int)step;
        }
        return haveFmt && haveData;
    }
}
