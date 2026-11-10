using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of the Mac's SpeechActivityGateTests plus the WAV trim and the never-block rules.</summary>
public class SpeechActivityTests
{
    private static SpeechSpan S(double start, double end) => new(start, end);

    [Fact]
    public void NoSegments_IsNoSpeech() =>
        Assert.Equal(SpeechVerdict.NoSpeech, SpeechActivity.Decide(Array.Empty<SpeechSpan>(), 11).Verdict);

    [Fact]
    public void SingleShortSegment_IsNoSpeech() =>
        Assert.Equal(SpeechVerdict.NoSpeech, SpeechActivity.Decide(new[] { S(0, 0.2) }, 11).Verdict);

    [Fact]
    public void ExactlyMinimumSpeech_IsNotNoSpeech() =>
        Assert.NotEqual(SpeechVerdict.NoSpeech, SpeechActivity.Decide(new[] { S(1, 1.25) }, 11).Verdict);

    [Fact]
    public void LongQuietEnds_AreTrimmed()
    {
        var d = SpeechActivity.Decide(new[] { S(0.5, 9.0) }, 11);
        Assert.Equal(SpeechVerdict.Trim, d.Verdict);
        Assert.Equal(0.1, d.Start, 3);
        Assert.Equal(9.6, d.End, 3);
    }

    [Fact]
    public void LittleRemovable_KeepsWhole() =>
        Assert.Equal(SpeechVerdict.Keep, SpeechActivity.Decide(new[] { S(0.3, 10.8) }, 11).Verdict);

    [Fact]
    public void TwoSegments_UseFirstStartAndLastEnd()
    {
        var d = SpeechActivity.Decide(new[] { S(1.0, 2.0), S(6.0, 7.0) }, 11);
        Assert.Equal(SpeechVerdict.Trim, d.Verdict);
        Assert.Equal(0.6, d.Start, 3);
        Assert.Equal(7.6, d.End, 3);
    }

    [Fact]
    public void Trim_IsClampedToTheRecording()
    {
        // Speech right at the start: no lead-in to cut, only the tail goes.
        var d = SpeechActivity.Decide(new[] { S(0.0, 1.0) }, 10);
        Assert.Equal(SpeechVerdict.Trim, d.Verdict);
        Assert.Equal(0, d.Start, 3);
        Assert.Equal(1.6, d.End, 3);
    }

    [Fact]
    public void Label_HasDurationsOnly()
    {
        Assert.Equal("noSpeech", SpeechDecision.NoSpeech.Label);
        Assert.Equal("keep", SpeechDecision.Keep.Label);
        Assert.Equal("trim(0.1-9.6)", new SpeechDecision(SpeechVerdict.Trim, 0.1, 9.6).Label);
    }

    [Fact]
    public void PadAndMerge_PadsClampsAndMergesTouchingSegments()
    {
        var merged = SpeechActivity.PadAndMerge(new[] { S(0.05, 1.0), S(1.1, 2.0), S(5.0, 5.4) }, 5.45);
        Assert.Equal(2, merged.Count);
        Assert.Equal(0, merged[0].Start, 3);
        Assert.Equal(2.1, merged[0].End, 3);
        Assert.Equal(4.9, merged[1].Start, 3);
        Assert.Equal(5.45, merged[1].End, 3);
    }

    private static byte[] Ramp(int samples, int rate = 16000, int channels = 1)
    {
        var data = new short[samples * channels];
        for (var i = 0; i < data.Length; i++) data[i] = (short)(i / channels % 30000);
        if (channels == 1) return Wav.Encode16(data, rate);
        var pcm = new byte[44 + data.Length * 2];
        Wav.Encode16(Array.Empty<short>(), rate).CopyTo(pcm, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(22), (short)channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(28), rate * 2 * channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(32), (short)(2 * channels));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(40), data.Length * 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(4), 36 + data.Length * 2);
        Buffer.BlockCopy(data, 0, pcm, 44, data.Length * 2);
        return pcm;
    }

    [Fact]
    public void TrimWav_KeepsExactlyTheFramesInRange()
    {
        var wav = Ramp(16000 * 4);
        var trimmed = SpeechActivity.TrimWav(wav, 1.0, 2.5);

        Assert.True(Wav.TryGetPcm16(trimmed, out var pcm));
        Assert.Equal(16000, pcm.SampleRate);
        Assert.Equal(1, pcm.Channels);
        Assert.Equal(24000 * 2, pcm.DataLength);
        Assert.Equal(1.5, Wav.DurationSeconds(trimmed), 3);
        // First kept sample is frame 16000 of the source.
        Assert.Equal((short)(16000 % 30000), BitConverter.ToInt16(trimmed, 44));
        Assert.Equal((short)((16000 + 23999) % 30000), BitConverter.ToInt16(trimmed, 44 + 23999 * 2));
    }

    [Fact]
    public void TrimWav_ClampsToTheFile()
    {
        var wav = Ramp(16000 * 2);
        var trimmed = SpeechActivity.TrimWav(wav, -3, 99);
        Assert.Equal(2.0, Wav.DurationSeconds(trimmed), 3);
    }

    [Fact]
    public void TrimWav_KeepsChannelLayout()
    {
        var wav = Ramp(16000, rate: 16000, channels: 2);
        var trimmed = SpeechActivity.TrimWav(wav, 0.25, 0.75);
        Assert.True(Wav.TryGetPcm16(trimmed, out var pcm));
        Assert.Equal(2, pcm.Channels);
        Assert.Equal(0.5, Wav.DurationSeconds(trimmed), 3);
    }

    [Fact]
    public void TrimWav_RejectsEmptyRangeAndNonPcm()
    {
        var wav = Ramp(16000);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechActivity.TrimWav(wav, 0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechActivity.TrimWav(wav, 5, 6));
        Assert.Throws<InvalidDataException>(() => SpeechActivity.TrimWav(new byte[] { 1, 2, 3 }, 0, 1));
    }

    [Fact]
    public void Gate_NoSpeech_ReturnsNull()
    {
        var wav = Ramp(16000 * 5);
        Assert.Null(SpeechActivityGate.Apply(wav, _ => Array.Empty<SpeechSpan>()));
    }

    [Fact]
    public void Gate_QuietEnds_ReturnTrimmedCopy()
    {
        var wav = Ramp(16000 * 11);
        var result = SpeechActivityGate.Apply(wav, _ => new[] { S(0.5, 9.0) });
        Assert.NotNull(result);
        Assert.NotSame(wav, result);
        Assert.Equal(9.5, Wav.DurationSeconds(result!), 3);
    }

    [Fact]
    public void Gate_Keep_ReturnsTheSameArray()
    {
        var wav = Ramp(16000 * 11);
        Assert.Same(wav, SpeechActivityGate.Apply(wav, _ => new[] { S(0.3, 10.8) }));
    }

    [Fact]
    public void Gate_DetectorFailure_UploadsTheOriginal()
    {
        var wav = Ramp(16000 * 3);
        Assert.Same(wav, SpeechActivityGate.Apply(wav, _ => throw new InvalidOperationException("model broke")));
    }

    [Fact]
    public void Gate_NotMono16k_SkipsTheDetector()
    {
        var called = false;
        IReadOnlyList<SpeechSpan> Detect(float[] _) { called = true; return Array.Empty<SpeechSpan>(); }
        var stereo = Ramp(16000, channels: 2);
        var rate44k = Ramp(44100, rate: 44100);
        var junk = new byte[] { 1, 2, 3, 4 };
        Assert.Same(stereo, SpeechActivityGate.Apply(stereo, Detect));
        Assert.Same(rate44k, SpeechActivityGate.Apply(rate44k, Detect));
        Assert.Same(junk, SpeechActivityGate.Apply(junk, Detect));
        Assert.False(called);
    }

    [Fact]
    public void Gate_BeforeTheModelIsLoaded_UploadsTheOriginal()
    {
        // Static gate, never prepared in this process unless the model test below ran first; both outcomes keep a loud WAV.
        var wav = Ramp(16000 * 2);
        if (!SpeechActivityGate.IsReady) Assert.Same(wav, SpeechActivityGate.Apply(wav));
    }

    [Fact]
    public void SileroCatalogEntry_IsPinned()
    {
        var file = Assert.Single(OfflineCatalog.SileroVad.Files);
        Assert.Equal("silero_vad.onnx", file.RelativePath);
        Assert.Equal(64, file.Sha256.Length);
        Assert.Equal(643854, file.Size);
        Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/", file.Url);
        Assert.True(NetworkPolicy.IsAllowedDownloadUrl(file.Url));
    }

    /// <summary>Runs the real model; skipped unless silero_vad.onnx is on this machine (env WSW_SILERO_VAD or the app's folder).</summary>
    [SileroModelFact]
    public void RealModel_SeparatesSpeechFromSilence()
    {
        using var vad = SileroVad.Load(SileroModelFactAttribute.ModelPath()!);

        var speech = File.ReadAllBytes(Paths.TestAudio("diktat.wav"));
        var samples = Wav.ReadMonoFloat(speech, out var rate);
        Assert.Equal(16000, rate);
        var spans = vad.Detect(samples);
        Assert.NotEmpty(spans);
        Assert.NotEqual(SpeechVerdict.NoSpeech, SpeechActivity.Decide(spans, (double)samples.Length / rate).Verdict);
        Assert.NotNull(SpeechActivityGate.Apply(speech, vad.Detect));
        foreach (var name in new[] { "angebot-ohne-markierung.wav", "anweisung-freundlicher.wav", "glossar-goedde.wav" })
            Assert.NotNull(SpeechActivityGate.Apply(File.ReadAllBytes(Paths.TestAudio(name)), vad.Detect));

        var quiet = File.ReadAllBytes(Paths.TestAudio("stille.wav"));
        Assert.Null(SpeechActivityGate.Apply(quiet, vad.Detect));
    }
}

/// <summary>Skipped when the Silero model is not on disk.</summary>
public sealed class SileroModelFactAttribute : FactAttribute
{
    public SileroModelFactAttribute()
    {
        if (ModelPath() == null) Skip = "silero_vad.onnx not found (set WSW_SILERO_VAD or let the app download it)";
    }

    public static string? ModelPath()
    {
        var env = Environment.GetEnvironmentVariable("WSW_SILERO_VAD");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var installed = Path.Combine(OfflineCatalog.SileroVad.FullPath, OfflineCatalog.SileroVad.Files[0].RelativePath);
        return File.Exists(installed) ? installed : null;
    }
}
