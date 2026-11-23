using SherpaOnnx;

namespace WhisperShortcut.Core;

/// <summary>
/// Silero VAD through sherpa-onnx: finds the speech in 16 kHz mono audio. One instance holds one
/// model; <see cref="Detect"/> is serialized because the detector is stateful.
/// </summary>
public sealed class SileroVad : IDisposable
{
    /// <summary>The only rate the model is fed; the microphone records it.</summary>
    public const int SampleRate = 16000;

    /// <summary>Samples per detector window; the Silero model only works on windows of exactly this size.</summary>
    private const int WindowSize = 512;

    private readonly VoiceActivityDetector _vad;
    private readonly object _lock = new();
    private bool _disposed;

    private SileroVad(VoiceActivityDetector vad) => _vad = vad;

    /// <summary>Loads silero_vad.onnx; throws when the file is missing or not a model.</summary>
    public static SileroVad Load(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Silero VAD model missing.", modelPath);
        var cfg = new VadModelConfig();
        cfg.SileroVad.Model = modelPath;
        cfg.SileroVad.Threshold = SpeechActivity.Threshold;
        cfg.SileroVad.MinSilenceDuration = 0.5f;
        cfg.SileroVad.MinSpeechDuration = 0.15f;
        cfg.SileroVad.MaxSpeechDuration = 600f; // do not cut one long monologue into 5 s pieces
        cfg.SileroVad.WindowSize = WindowSize;
        cfg.SampleRate = SampleRate;
        cfg.NumThreads = 1;
        cfg.Provider = "cpu";
        return new SileroVad(new VoiceActivityDetector(cfg, 60f));
    }

    /// <summary>
    /// Speech stretches of the samples, in order, padded by <see cref="SpeechActivity.SegmentPaddingSeconds"/> and merged.
    /// The samples are fed in 512-sample windows because sherpa-onnx returns one bogus segment at the very end when it gets a whole recording at once.
    /// </summary>
    public IReadOnlyList<SpeechSpan> Detect(float[] samples)
    {
        var raw = new List<SpeechSpan>();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vad.Reset();
            foreach (var window in Windows(samples, WindowSize))
            {
                _vad.AcceptWaveform(window);
                Drain(raw);
            }
            _vad.Flush();
            Drain(raw);
            _vad.Reset();
        }
        return SpeechActivity.PadAndMerge(raw, (double)samples.Length / SampleRate);
    }

    private void Drain(List<SpeechSpan> raw)
    {
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front();
            raw.Add(new SpeechSpan((double)seg.Start / SampleRate, (double)(seg.Start + seg.Samples.Length) / SampleRate));
            _vad.Pop();
        }
    }

    /// <summary>Consecutive windows of exactly <paramref name="size"/> samples; the last one is zero-padded.</summary>
    internal static IEnumerable<float[]> Windows(float[] samples, int size)
    {
        for (var i = 0; i < samples.Length; i += size)
        {
            var window = new float[size];
            Array.Copy(samples, i, window, 0, Math.Min(size, samples.Length - i));
            yield return window;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _vad.Dispose();
        }
    }
}
