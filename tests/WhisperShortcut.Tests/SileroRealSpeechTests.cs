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

        foreach (var t in truth)
        {
            var len = t[1] - t[0];
            var covered = spans.Sum(s => Math.Max(0, Math.Min(s.End, t[1]) - Math.Max(s.Start, t[0])));
            Assert.True(covered >= MinCoverage * len,
                $"{what}; only {F(covered)} s of {F(t[0])}–{F(t[1])} are covered ({covered / len:P0}, need {MinCoverage:P0})");
        }
        Assert.NotEmpty(spans);
        Assert.True(spans[0].Start <= truth[0][0] + EdgeTolerance, $"{what}; first span starts too late");
        Assert.True(spans[^1].End >= truth[^1][1] - EdgeTolerance, $"{what}; last span ends too early");
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
