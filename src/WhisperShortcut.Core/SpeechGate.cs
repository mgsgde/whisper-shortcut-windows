using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WhisperShortcut.Core;

/// <summary>Level statistics of a recording, 30 ms frames.</summary>
/// <param name="PeakDbfs">Loudest sample in dBFS (-infinity for digital silence).</param>
/// <param name="LoudFrames">Frames whose RMS is above <see cref="SpeechGate.ThresholdDbfs"/>.</param>
/// <param name="Frames">All frames.</param>
public readonly record struct SpeechLevel(double PeakDbfs, int LoudFrames, int Frames);

/// <summary>
/// Decides before the Gemini call whether a recording holds any speech. Without it a quiet room (a
/// click, a breath) goes out as audio and the model fills it with glossary words. Same thresholds as
/// the Mac: at least 150 ms of 30 ms frames above -45 dBFS.
/// </summary>
public static class SpeechGate
{
    /// <summary>Frame RMS above this counts as sound.</summary>
    public const double ThresholdDbfs = -45.0;

    /// <summary>Frames above the threshold needed for speech (5 x 30 ms), so one click does not count.</summary>
    public const int MinLoudFrames = 5;

    private const int FrameMs = 30;
    private static readonly double ThresholdLinear = Math.Pow(10, ThresholdDbfs / 20);

    /// <summary>
    /// Measures the recording. 16-bit PCM is scanned in place (the microphone format) so a long
    /// dictation does not allocate a float copy just to decide whether to skip the request.
    /// Anything that is not 16-bit PCM reports as loud: better a wasted request than a lost dictation.
    /// </summary>
    public static SpeechLevel Analyze(byte[] wav)
    {
        if (!Wav.TryGetPcm16(wav, out var pcm))
            return new SpeechLevel(0, MinLoudFrames, MinLoudFrames);

        var frameLen = Math.Max(1, pcm.SampleRate * FrameMs / 1000);
        var data = wav.AsSpan(pcm.DataOffset, pcm.DataLength);
        return pcm.Channels == 1 && BitConverter.IsLittleEndian
            ? AnalyzeMono(MemoryMarshal.Cast<byte, short>(data), frameLen)
            : AnalyzeFallback(data, pcm.Channels, frameLen);
    }

    /// <summary>True when enough of the recording is above the threshold to be speech.</summary>
    public static bool HasSpeech(SpeechLevel level) => level.LoudFrames >= MinLoudFrames;

    /// <summary>Convenience: analyze and judge in one call.</summary>
    public static bool HasSpeech(byte[] wav) => HasSpeech(Analyze(wav));

    // Integer RMS: sqrt(sum(s^2)/n)/32768 > T  <=>  sum(s^2) > T^2 * 32768^2 * n.
    // Same decision as the old float-array path away from the exact threshold; no 1.9 MB alloc.
    private static SpeechLevel AnalyzeMono(ReadOnlySpan<short> samples, int frameLen)
    {
        int frames = 0, loud = 0;
        int peak = 0;
        var thresholdEnergy = ThresholdLinear * ThresholdLinear * 32768.0 * 32768.0 * frameLen;
        var i = 0;
        while (i + frameLen <= samples.Length)
        {
            long sumSq = 0;
            var end = i + frameLen;
            for (; i < end; i++)
            {
                int s = samples[i];
                sumSq += (long)s * s;
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            frames++;
            if (sumSq > thresholdEnergy) loud++;
        }
        var peakF = peak / 32768.0;
        return new SpeechLevel(peakF > 0 ? 20 * Math.Log10(peakF) : double.NegativeInfinity, loud, frames);
    }

    private static SpeechLevel AnalyzeFallback(ReadOnlySpan<byte> data, int channels, int frameLen)
    {
        var framesTotal = data.Length / (2 * channels);
        int frames = 0, loud = 0;
        float peak = 0;
        var start = 0;
        while (start + frameLen <= framesTotal)
        {
            double sum = 0;
            for (var i = start; i < start + frameLen; i++)
            {
                var acc = 0;
                for (var c = 0; c < channels; c++)
                    acc += BinaryPrimitives.ReadInt16LittleEndian(data.Slice((i * channels + c) * 2, 2));
                var s = acc / (float)channels / 32768f;
                sum += (double)s * s;
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            frames++;
            if (Math.Sqrt(sum / frameLen) > ThresholdLinear) loud++;
            start += frameLen;
        }
        return new SpeechLevel(peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity, loud, frames);
    }
}
