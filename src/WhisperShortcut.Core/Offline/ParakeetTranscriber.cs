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

    private bool _disposed;

    private ParakeetTranscriber(OfflineRecognizer recognizer, IReadOnlyList<string> hotwords)
    {
        _recognizer = recognizer;
        Hotwords = hotwords;
    }

    /// <summary>The Glossary terms this instance was loaded with (empty when none); sherpa-onnx cannot change them later.</summary>
    public IReadOnlyList<string> Hotwords { get; }

    /// <summary>
    /// Loads the model files from <paramref name="directory"/>; throws when one is missing.
    /// With <paramref name="hotwords"/> the recognizer uses beam search biased towards those terms;
    /// <paramref name="workDir"/> then receives bpe.vocab and the hotwords file (never the model directory).
    /// </summary>
    /// <param name="hotwordsFileName">File name inside <paramref name="workDir"/>; distinct per load so a reload cannot overwrite the file the running instance reads.</param>
    public static ParakeetTranscriber Load(string directory, int threads,
        IReadOnlyList<string>? hotwords = null, string? workDir = null, string hotwordsFileName = "hotwords.txt")
    {
        hotwords ??= Array.Empty<string>();
        if (hotwords.Count > 0 && string.IsNullOrWhiteSpace(workDir))
            throw new ArgumentException("A work directory is required when hotwords are given.", nameof(workDir));

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
        if (hotwords.Count == 0)
        {
            cfg.DecodingMethod = "greedy_search";
        }
        else
        {
            Directory.CreateDirectory(workDir!);
            var vocab = Path.Combine(workDir!, "bpe.vocab");
            var hotFile = Path.Combine(workDir!, hotwordsFileName);
            ParakeetHotwords.WriteBpeVocab(tokens, vocab);
            File.WriteAllText(hotFile, string.Join("\n", hotwords) + "\n", new System.Text.UTF8Encoding(false));
            cfg.ModelConfig.ModelingUnit = "bpe";
            cfg.ModelConfig.BpeVocab = vocab;
            cfg.DecodingMethod = "modified_beam_search";
            cfg.MaxActivePaths = 4;
            cfg.HotwordsFile = hotFile;
            cfg.HotwordsScore = ParakeetHotwords.Score;
        }
        return new ParakeetTranscriber(new OfflineRecognizer(cfg), hotwords.ToArray());
    }

    /// <summary>Transcribes a 16-bit PCM WAV; returns the trimmed text, "" for silence.</summary>
    public string Transcribe(byte[] wav) => TranscribeDetailed(wav).Text;

    /// <summary>
    /// Like <see cref="Transcribe"/>, plus the token timings sherpa-onnx reports (for
    /// <see cref="ParakeetPunctuationTiming"/>). Tokens is null when they are missing or implausible:
    /// durations are only filled for TDT greedy decoding, so a glossary (beam search) load has none.
    /// </summary>
    public ParakeetDecode TranscribeDetailed(byte[] wav)
    {
        var samples = Wav.ReadMonoFloat(wav, out var rate);
        if (samples.Length == 0) return new ParakeetDecode("", null, 0);
        var audioSeconds = samples.Length / (double)rate;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var stream = _recognizer.CreateStream();
            // Another rate than 16 kHz is passed as is; sherpa-onnx resamples.
            stream.AcceptWaveform(rate, samples);
            _recognizer.Decode(stream);
            var result = stream.Result;
            return new ParakeetDecode((result.Text ?? "").Trim(), BuildTokens(result, audioSeconds), audioSeconds);
        }
    }

    private static IReadOnlyList<ParakeetPunctuationTiming.Token>? BuildTokens(OfflineRecognizerResult result, double audioSeconds)
    {
        var tokens = result.Tokens;
        var starts = result.Timestamps;
        var durations = result.Durations;
        if (tokens == null || starts == null || durations == null) return null;
        if (tokens.Length == 0 || tokens.Length != starts.Length || tokens.Length != durations.Length) return null;
        var list = new List<ParakeetPunctuationTiming.Token>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            double start = starts[i];
            double end = start + durations[i];
            // A unit mismatch (e.g. frames instead of seconds) would run far past the audio.
            if (end > audioSeconds + 0.5) return null;
            list.Add(new ParakeetPunctuationTiming.Token(tokens[i], start, end));
        }
        return list;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _recognizer.Dispose();
        }
    }
}

/// <summary>One decode: the text, the token timings when sherpa-onnx gave usable ones, and the audio length in seconds.</summary>
public sealed record ParakeetDecode(string Text, IReadOnlyList<ParakeetPunctuationTiming.Token>? Tokens, double AudioSeconds);
