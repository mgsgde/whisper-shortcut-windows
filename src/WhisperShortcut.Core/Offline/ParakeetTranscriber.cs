using SherpaOnnx;

namespace WhisperShortcut.Core;

/// <summary>
/// Offline speech recognition with NVIDIA Parakeet TDT 0.6B v3 (int8 ONNX) through sherpa-onnx.
/// Loading takes a moment, so keep one instance alive; <see cref="Transcribe"/> is serialized.
/// </summary>
public sealed class ParakeetTranscriber : IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private readonly object _gate = new();

    private ParakeetTranscriber(OfflineRecognizer recognizer) => _recognizer = recognizer;

    /// <summary>Loads the model files from <paramref name="directory"/>; throws when one is missing.</summary>
    public static ParakeetTranscriber Load(string directory, int threads)
    {
        var encoder = Path.Combine(directory, "encoder.int8.onnx");
        var decoder = Path.Combine(directory, "decoder.int8.onnx");
        var joiner = Path.Combine(directory, "joiner.int8.onnx");
        var tokens = Path.Combine(directory, "tokens.txt");
        foreach (var f in new[] { encoder, decoder, joiner, tokens })
            if (!File.Exists(f)) throw new FileNotFoundException("Parakeet model file missing.", f);

        var cfg = new OfflineRecognizerConfig();
        cfg.FeatConfig.SampleRate = 16000;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Transducer.Encoder = encoder;
        cfg.ModelConfig.Transducer.Decoder = decoder;
        cfg.ModelConfig.Transducer.Joiner = joiner;
        cfg.ModelConfig.Tokens = tokens;
        cfg.ModelConfig.ModelType = "nemo_transducer";
        cfg.ModelConfig.NumThreads = Math.Max(1, threads);
        cfg.DecodingMethod = "greedy_search";
        return new ParakeetTranscriber(new OfflineRecognizer(cfg));
    }

    /// <summary>Transcribes a 16-bit PCM WAV; returns the trimmed text, "" for silence.</summary>
    public string Transcribe(byte[] wav)
    {
        var samples = Wav.ReadMonoFloat(wav, out var rate);
        if (samples.Length == 0) return "";
        lock (_gate)
        {
            using var stream = _recognizer.CreateStream();
            // Another rate than 16 kHz is passed as is; sherpa-onnx resamples.
            stream.AcceptWaveform(rate, samples);
            _recognizer.Decode(stream);
            return (stream.Result.Text ?? "").Trim();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) _recognizer.Dispose();
    }
}
