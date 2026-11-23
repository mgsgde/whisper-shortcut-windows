using System.Globalization;
using System.Text.Json;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Real Silero model on real (TTS) speech with known positions. The fake-detector tests cannot see
/// a detector that misreads the audio: 0.7.13 fed the whole recording in one call, the VAD reported
/// one bogus segment at the end and the gate trimmed the upload to a single word.
/// </summary>
public class SileroRealSpeechTests
{
    private const double EdgeTolerance = 0.3;
    private const double MinCoverage = 0.85;

    [SileroModelFact]
    public void Satz_SpeechIsFoundWhereItIs() => CheckDetect("satz-hedda");

    [SileroModelFact]
    public void Pause_BothPhrasesAreFound() => CheckDetect("pause-hedda");

    [SileroModelFact]
    public void Satz_GateKeepsAllSpeech() => CheckGate("satz-hedda");

    [SileroModelFact]
    public void Pause_GateKeepsAllSpeech() => CheckGate("pause-hedda");

    /// <summary>A real microphone: quiet speech (median −48 dBFS), 86 s in one piece, 13 stretches with pauses.</summary>
    [SileroModelFact]
    public void Mikro_QuietLong_SpeechIsFoundWhereItIs() => CheckDetect("mikro-leise");

    [SileroModelFact]
    public void Mikro_QuietLong_GateKeepsAllSpeech() => CheckGate("mikro-leise");

    /// <summary>
    /// TTS is loud and clean; this covers a quiet, noisy, long dictation (over a minute in one piece,
    /// the length of real dictations) until real microphone recordings exist.
    /// </summary>
    [SileroModelFact]
    public void LongQuietNoisy_AllPhrasesAreFound()
    {
        const int repeats = 7;
        const double scale = 0.1;     // -20 dB: quiet speaker / far microphone
        const float noise = 0.003f;   // uniform +-0.003, roughly -50 dBFS
        var (wav, clip, clipTruth) = Load("pause-hedda");
        var clipSeconds = Wav.DurationSeconds(wav);
        var rng = new Random(42);

        var samples = new float[clip.Length * repeats];
        for (var k = 0; k < repeats; k++)
            for (var i = 0; i < clip.Length; i++)
                samples[k * clip.Length + i] = (float)(clip[i] * scale) + (float)((rng.NextDouble() * 2 - 1) * noise);
        var truth = Enumerable.Range(0, repeats)
            .SelectMany(k => clipTruth.Select(t => new[] { t[0] + k * clipSeconds, t[1] + k * clipSeconds }))
            .ToArray();
        Assert.True(samples.Length / 16000.0 > 60, "test recording must be longer than a minute");

        using var vad = LoadVad();
        var spans = vad.Detect(samples);
        AssertCovered(spans, truth, 0.75, $"Silero found {Fmt(spans)} but speech is {Fmt(truth)}");
    }

    private static (byte[] Wav, float[] Samples, double[][] Truth) Load(string name)
    {
        var wav = File.ReadAllBytes(Paths.TestAudio(name + ".wav"));
        var samples = Wav.ReadMonoFloat(wav, out var rate);
        Assert.Equal(16000, rate);
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.TestAudio(name + ".json")));
        var truth = doc.RootElement.GetProperty("speech").EnumerateArray()
            .Select(i => new[] { i[0].GetDouble(), i[1].GetDouble() }).ToArray();
        return (wav, samples, truth);
    }

    private static SileroVad LoadVad()
    {
        var path = SileroModelFactAttribute.ModelPath();
        Assert.True(path != null, "WSW_REQUIRE_SILERO=1 but silero_vad.onnx was not found (set WSW_SILERO_VAD).");
        return SileroVad.Load(path!);
    }

    private static string Fmt(IEnumerable<SpeechSpan> spans) =>
        "[" + string.Join(", ", spans.Select(s => F(s.Start) + "–" + F(s.End))) + "]";

    private static string Fmt(IEnumerable<double[]> truth) =>
        "[" + string.Join(", ", truth.Select(t => F(t[0]) + "–" + F(t[1]))) + "]";

    private static string F(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);

    private static void CheckDetect(string name)
    {
        var (_, samples, truth) = Load(name);
        using var vad = LoadVad();
        var spans = vad.Detect(samples);
        var what = $"Silero found {Fmt(spans)} but speech is {Fmt(truth)}";

        AssertCovered(spans, truth, MinCoverage, what);
        Assert.NotEmpty(spans);
        Assert.True(spans[0].Start <= truth[0][0] + EdgeTolerance, $"{what}; first span starts too late");
        Assert.True(spans[^1].End >= truth[^1][1] - EdgeTolerance, $"{what}; last span ends too early");
    }

    private static void AssertCovered(IReadOnlyList<SpeechSpan> spans, double[][] truth, double minCoverage, string what)
    {
        foreach (var t in truth)
        {
            var len = t[1] - t[0];
            var covered = spans.Sum(s => Math.Max(0, Math.Min(s.End, t[1]) - Math.Max(s.Start, t[0])));
            Assert.True(covered >= minCoverage * len,
                $"{what}; only {F(covered)} s of {F(t[0])}–{F(t[1])} are covered ({covered / len:P0}, need {minCoverage:P0})");
        }
    }

    private static void CheckGate(string name)
    {
        var (wav, samples, truth) = Load(name);
        using var vad = LoadVad();
        var trimmed = SpeechActivityGate.Apply(wav, vad.Detect);
        var what = $"speech is {Fmt(truth)}, Silero found {Fmt(vad.Detect(samples))}";
        Assert.True(trimmed != null, $"gate dropped the whole recording; {what}");

        var original = Wav.DurationSeconds(wav);
        var kept = Wav.DurationSeconds(trimmed!);
        var needed = truth[^1][1] - truth[0][0];
        Assert.True(kept >= needed - 0.2, $"gate kept {F(kept)} s but the speech spans {F(needed)} s; {what}");
        Assert.True(kept < original, $"gate did not trim the 2 s silences ({F(kept)} s of {F(original)} s); {what}");
    }
}
