using System.Buffers.Binary;

namespace WhisperShortcut.Core;

/// <summary>One stretch of speech found by the VAD, in seconds from the start of the recording.</summary>
public readonly record struct SpeechSpan(double Start, double End)
{
    /// <summary>End minus start.</summary>
    public double Length => End - Start;
}

/// <summary>What the speech gate does with one recording or chunk.</summary>
public enum SpeechVerdict
{
    /// <summary>Not a dictation: do not upload.</summary>
    NoSpeech,
    /// <summary>Upload as recorded.</summary>
    Keep,
    /// <summary>Upload only [Start, End).</summary>
    Trim,
}

/// <summary>Result of <see cref="SpeechActivity.Decide"/>; Start and End are seconds and only meaningful for <see cref="SpeechVerdict.Trim"/>.</summary>
public readonly record struct SpeechDecision(SpeechVerdict Verdict, double Start = 0, double End = 0)
{
    /// <summary>Not a dictation.</summary>
    public static SpeechDecision NoSpeech => new(SpeechVerdict.NoSpeech);

    /// <summary>Upload unchanged.</summary>
    public static SpeechDecision Keep => new(SpeechVerdict.Keep);

    /// <summary>Short log label, durations only: "noSpeech", "keep", "trim(0.1-9.6)".</summary>
    public string Label => Verdict switch
    {
        SpeechVerdict.NoSpeech => "noSpeech",
        SpeechVerdict.Keep => "keep",
        _ => FormattableString.Invariant($"trim({Start:0.0}-{End:0.0})"),
    };
}

/// <summary>
/// Pure rules of the Silero speech gate (Mac SpeechActivityGate): no model, no audio device.
/// The VAD itself lives in <see cref="SileroVad"/>.
/// </summary>
public static class SpeechActivity
{
    /// <summary>Less speech than this is not a dictation (seconds).</summary>
    public const double MinSpeechSeconds = 0.25;

    /// <summary>Quiet kept in front of the first speech when trimming (seconds).</summary>
    public const double LeadInSeconds = 0.4;

    /// <summary>Quiet kept behind the last speech when trimming (seconds).</summary>
    public const double TailSeconds = 0.6;

    /// <summary>Trimming only pays off when it removes at least this much (seconds).</summary>
    public const double MinRemovedSeconds = 1.5;

    /// <summary>Silero's standard threshold. A higher one drops quiet speech, and a miss here is a lost dictation.</summary>
    public const float Threshold = 0.5f;

    /// <summary>Padding the VAD adds to both ends of every segment (seconds), like the Mac's speechPadding.</summary>
    public const double SegmentPaddingSeconds = 0.1;

    /// <summary>
    /// Under 0.25 s of speech is no dictation. Otherwise keep a 0.4 s lead-in and a 0.6 s tail and
    /// trim only when that still removes at least 1.5 s. <paramref name="segments"/> are in time order
    /// (first start, last end), which is what the VAD returns.
    /// </summary>
    public static SpeechDecision Decide(IReadOnlyList<SpeechSpan> segments, double duration)
    {
        double speech = 0;
        foreach (var s in segments) speech += s.End - s.Start;
        if (speech < MinSpeechSeconds || segments.Count == 0) return SpeechDecision.NoSpeech;

        var start = Math.Max(0, segments[0].Start - LeadInSeconds);
        var end = Math.Min(duration, segments[^1].End + TailSeconds);
        if (start + (duration - end) >= MinRemovedSeconds) return new SpeechDecision(SpeechVerdict.Trim, start, end);
        return SpeechDecision.Keep;
    }

    /// <summary>
    /// Widens each segment by <paramref name="padding"/> on both sides (clamped to the recording) and
    /// merges the ones that now touch, so overlapping padding is not counted as speech twice.
    /// </summary>
    public static List<SpeechSpan> PadAndMerge(IEnumerable<SpeechSpan> raw, double duration, double padding = SegmentPaddingSeconds)
    {
        var result = new List<SpeechSpan>();
        foreach (var s in raw.OrderBy(x => x.Start))
        {
            var padded = new SpeechSpan(Math.Max(0, s.Start - padding), Math.Min(duration, s.End + padding));
            if (padded.End <= padded.Start) continue;
            if (result.Count > 0 && padded.Start <= result[^1].End)
                result[^1] = new SpeechSpan(result[^1].Start, Math.Max(result[^1].End, padded.End));
            else
                result.Add(padded);
        }
        return result;
    }

    /// <summary>
    /// Copies the frames [start, end) (seconds) of a 16-bit PCM WAV into a new standalone WAV.
    /// Throws <see cref="InvalidDataException"/> for other formats and <see cref="ArgumentOutOfRangeException"/>
    /// when the range holds no frame; the caller then uploads the original.
    /// </summary>
    public static byte[] TrimWav(byte[] wav, double start, double end)
    {
        if (!Wav.TryGetPcm16(wav, out var pcm)) throw new InvalidDataException("Only 16-bit PCM WAV can be trimmed.");
        var frameBytes = pcm.FrameBytes;
        var totalFrames = pcm.DataLength / frameBytes;
        var startFrame = Math.Clamp((long)Math.Floor(start * pcm.SampleRate), 0, totalFrames);
        var endFrame = Math.Clamp((long)Math.Floor(end * pcm.SampleRate), startFrame, totalFrames);
        if (endFrame <= startFrame) throw new ArgumentOutOfRangeException(nameof(end), "Empty trim range.");

        var length = checked((int)((endFrame - startFrame) * frameBytes));
        var result = new byte[44 + length];
        var h = result.AsSpan();
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + length);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)pcm.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], pcm.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], pcm.SampleRate * frameBytes);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)frameBytes);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], length);
        Buffer.BlockCopy(wav, pcm.DataOffset + (int)(startFrame * frameBytes), result, 44, length);
        return result;
    }
}
