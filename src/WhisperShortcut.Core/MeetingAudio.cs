namespace WhisperShortcut.Core;

/// <summary>Which audio a Live Meeting records.</summary>
public enum MeetingAudioPreference
{
    /// <summary>Decide from the default output device.</summary>
    Automatic,

    /// <summary>Microphone only.</summary>
    MicrophoneOnly,

    /// <summary>Microphone plus what the PC plays.</summary>
    MicrophoneAndSystem,
}

/// <summary>Decides whether a meeting mixes the PC's output into the microphone, and names the choices for storage and UI.</summary>
public static class MeetingAudioRouting
{
    /// <summary>Windows EndpointFormFactor: built-in or external speakers.</summary>
    private const int FormFactorSpeakers = 1;

    /// <summary>Windows EndpointFormFactor: HDMI monitor or TV, which plays through speakers too.</summary>
    private const int FormFactorDigitalAudioDisplay = 9;

    /// <summary>Parses the stored name ("auto", "mic", "mix"); null, blank or unknown gives <see cref="MeetingAudioPreference.Automatic"/>.</summary>
    public static MeetingAudioPreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mic" => MeetingAudioPreference.MicrophoneOnly,
        "mix" => MeetingAudioPreference.MicrophoneAndSystem,
        _ => MeetingAudioPreference.Automatic,
    };

    /// <summary>The storage name of a preference.</summary>
    public static string Name(MeetingAudioPreference p) => p switch
    {
        MeetingAudioPreference.MicrophoneOnly => "mic",
        MeetingAudioPreference.MicrophoneAndSystem => "mix",
        _ => "auto",
    };

    /// <summary>True when the output device is a speaker (form factor 1) or an HDMI display (9). Null = unknown = false.</summary>
    public static bool IsLikelySpeaker(int? formFactor) =>
        formFactor is FormFactorSpeakers or FormFactorDigitalAudioDisplay;

    /// <summary>
    /// Whether the PC's output is mixed into the recording. Automatic mixes unless the output is a speaker:
    /// with speakers the microphone already hears the other side, and mixing would record it twice.
    /// With headphones, a headset or an unknown device the mic cannot hear it, so it is mixed.
    /// </summary>
    public static bool MixesSystemAudio(MeetingAudioPreference p, bool hasOutputDevice, int? outputFormFactor) => p switch
    {
        MeetingAudioPreference.MicrophoneOnly => false,
        MeetingAudioPreference.MicrophoneAndSystem => true,
        _ => hasOutputDevice && !IsLikelySpeaker(outputFormFactor),
    };

    /// <summary>German label for the UI.</summary>
    public static string Label(bool mixesSystemAudio) => mixesSystemAudio ? "Mikrofon + PC-Ton" : "nur Mikrofon";

    /// <summary>Value stored in meeting.json.</summary>
    public static string MetadataName(bool mixesSystemAudio) => mixesSystemAudio ? "mic+system" : "mic";
}

/// <summary>Sample-format helpers for mixing the microphone with the system audio.</summary>
public static class MeetingAudioMixer
{
    /// <summary>Adds <paramref name="system"/> (-1..1) to the 16-bit <paramref name="mic"/> in place, clamped; a shorter system span counts as silence.</summary>
    public static void MixInto(Span<short> mic, ReadOnlySpan<float> system)
    {
        for (var i = 0; i < mic.Length; i++)
        {
            var sys = i < system.Length ? system[i] : 0f;
            var v = Math.Clamp(mic[i] / 32768f + sys, -1f, 1f);
            mic[i] = (short)Math.Round(v * 32767f);
        }
    }

    /// <summary>Averages the channels of each frame. One channel or fewer is a plain copy; a trailing partial frame is ignored.</summary>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1) return interleaved.ToArray();
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    /// <summary>Decodes 16-bit little-endian PCM bytes to floats in -1..1.</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> bytes)
    {
        var result = new float[bytes.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) / 32768f;
        return result;
    }

    /// <summary>Decodes 32-bit IEEE float little-endian bytes.</summary>
    public static float[] Float32ToFloat(ReadOnlySpan<byte> bytes)
    {
        var result = new float[bytes.Length / 4];
        for (var i = 0; i < result.Length; i++)
            result[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
        return result;
    }

    /// <summary>RMS level in dBFS, floored at -160 (also for empty or all-zero input).</summary>
    public static float RmsDb(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return -160f;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        if (rms <= 0) return -160f;
        return (float)Math.Max(-160.0, 20 * Math.Log10(rms / 32768.0));
    }
}

/// <summary>
/// Linear-interpolation resampler for one continuous mono stream. The read position and the last input
/// sample carry across calls, so buffer boundaries do not click and the output count follows the rate ratio.
/// </summary>
public sealed class StreamResampler
{
    private readonly int _inRate;
    private readonly int _outRate;
    private readonly double _step;     // input samples per output sample
    private double _pos;               // next output position, in input samples relative to the start of the next input (may be -1..0 = between last and first)
    private float _last;
    private bool _hasLast;

    /// <summary>Creates a resampler from <paramref name="inRate"/> to <paramref name="outRate"/> Hz.</summary>
    public StreamResampler(int inRate, int outRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outRate, 0);
        _inRate = inRate;
        _outRate = outRate;
        _step = (double)inRate / outRate;
    }

    /// <summary>Resamples the next piece of the stream. Equal rates return a copy.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (_inRate == _outRate) return input.ToArray();
        if (input.Length == 0) return Array.Empty<float>();

        // Position p refers to input index p of this call; index -1 is the last sample of the previous call.
        var output = new List<float>((int)(input.Length / _step) + 2);
        while (true)
        {
            var i0 = (int)Math.Floor(_pos);
            if (i0 + 1 >= input.Length) break;
            var frac = (float)(_pos - i0);
            float a = i0 < 0 ? (_hasLast ? _last : input[0]) : input[i0];
            var b = input[i0 + 1];
            output.Add(a + (b - a) * frac);
            _pos += _step;
        }
        _last = input[^1];
        _hasLast = true;
        _pos -= input.Length;
        return output.ToArray();
    }
}

/// <summary>
/// Thread-safe FIFO of 16 kHz mono floats between the loopback callback (<see cref="Push"/>) and the mic
/// callback (<see cref="Take"/>). The mic is the clock. Loopback delivers nothing while the PC plays nothing,
/// so Take pads with zeros. When more than the limit is queued (the two clocks drift), the oldest samples go.
/// </summary>
public sealed class SystemAudioBuffer
{
    private readonly object _gate = new();
    private readonly Queue<float> _queue = new();
    private readonly int _maxSamples;

    /// <summary>Creates a buffer holding at most <paramref name="maxSamples"/> (default 1 s at 16 kHz).</summary>
    public SystemAudioBuffer(int maxSamples = 16000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSamples, 0);
        _maxSamples = maxSamples;
    }

    /// <summary>Queues samples, dropping the oldest beyond the limit.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (var s in samples) _queue.Enqueue(s);
            while (_queue.Count > _maxSamples) _queue.Dequeue();
        }
    }

    /// <summary>Fills <paramref name="destination"/> from the queue and zero-fills the rest; returns the number really taken.</summary>
    public int Take(Span<float> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(destination.Length, _queue.Count);
            for (var i = 0; i < n; i++) destination[i] = _queue.Dequeue();
            destination[n..].Clear();
            return n;
        }
    }

    /// <summary>Samples currently queued.</summary>
    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>Drops everything queued.</summary>
    public void Clear()
    {
        lock (_gate) _queue.Clear();
    }
}
