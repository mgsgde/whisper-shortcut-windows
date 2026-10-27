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

    /// <summary>Measures the recording. Anything that is not 16-bit PCM reports as loud: better a wasted request than a lost dictation.</summary>
    public static SpeechLevel Analyze(byte[] wav)
    {
        float[] samples;
        int rate;
        try
        {
            samples = Wav.ReadMonoFloat(wav, out rate);
        }
        catch (InvalidDataException)
        {
            return new SpeechLevel(0, MinLoudFrames, MinLoudFrames);
        }

        var frameLen = Math.Max(1, rate * FrameMs / 1000);
        int frames = 0, loud = 0;
        float peak = 0;
        for (var start = 0; start + frameLen <= samples.Length; start += frameLen)
        {
            double sum = 0;
            for (var i = start; i < start + frameLen; i++)
            {
                var s = samples[i];
                sum += (double)s * s;
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            frames++;
            if (Math.Sqrt(sum / frameLen) > ThresholdLinear) loud++;
        }
        return new SpeechLevel(peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity, loud, frames);
    }

    /// <summary>True when enough of the recording is above the threshold to be speech.</summary>
    public static bool HasSpeech(SpeechLevel level) => level.LoudFrames >= MinLoudFrames;

    /// <summary>Convenience: analyze and judge in one call.</summary>
    public static bool HasSpeech(byte[] wav) => HasSpeech(Analyze(wav));
}
