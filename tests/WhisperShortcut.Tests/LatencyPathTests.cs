using System.Buffers.Binary;
using System.Diagnostics;
using WhisperShortcut.Core;
using Xunit.Abstractions;

namespace WhisperShortcut.Tests;

/// <summary>
/// Correctness plus measurements for the Stop → request path: speech gate, WAV decode, Gemini JSON.
/// Numbers are written to the test output so a review can see the cost, not just a glance at the diff.
/// </summary>
public class LatencyPathTests
{
    private readonly ITestOutputHelper _out;
    public LatencyPathTests(ITestOutputHelper output) => _out = output;

    private const int Rate = 16000;

    [Fact]
    public void TryGetPcm16_AndDuration_MatchMicFormat()
    {
        var wav = Tone(seconds: 2, rmsDbfs: -30);
        Assert.True(Wav.TryGetPcm16(wav, out var pcm));
        Assert.Equal(Rate, pcm.SampleRate);
        Assert.Equal(1, pcm.Channels);
        Assert.Equal(44, pcm.DataOffset);
        Assert.Equal(Rate * 2 * 2, pcm.DataLength);
        Assert.Equal(2.0, Wav.DurationSeconds(wav), 1e-6);
        Assert.False(Wav.TryGetPcm16(new byte[10], out _));
    }

    [Fact]
    public void SpeechGate_MatchesFloatArrayReference_MonoAndStereo()
    {
        foreach (var channels in new[] { 1, 2 })
        {
            var wav = Tone(seconds: 1.2, rmsDbfs: -30, channels: channels);
            var expected = ReferenceAnalyze(wav);
            var actual = SpeechGate.Analyze(wav);
            Assert.Equal(expected.Frames, actual.Frames);
            Assert.Equal(expected.LoudFrames, actual.LoudFrames);
            Assert.Equal(expected.PeakDbfs, actual.PeakDbfs, 1e-6);
            Assert.Equal(SpeechGate.HasSpeech(expected), SpeechGate.HasSpeech(actual));
        }
    }

    [Fact]
    public void SpeechGate_ThirtySeconds_DoesNotAllocateAFloatCopy()
    {
        var wav = Tone(seconds: 30, rmsDbfs: -30);
        SpeechGate.Analyze(wav); // JIT / type init
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var level = SpeechGate.Analyze(wav);
        sw.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"SpeechGate 30 s: {sw.Elapsed.TotalMilliseconds:F2} ms, {alloc} bytes allocated");
        Assert.True(SpeechGate.HasSpeech(level));
        // A float[] of this recording would be 1.92 MB. Stay well under a 64 KB header-parse budget.
        Assert.True(alloc < 64_000, $"SpeechGate allocated {alloc} bytes for a 30 s file");
        Assert.True(sw.ElapsedMilliseconds < 200, $"SpeechGate 30 s took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ReadMonoFloat_ThirtySeconds_MatchesSlowPathAndIsBounded()
    {
        var wav = Tone(seconds: 30, rmsDbfs: -20);
        var expected = ReferenceReadMonoFloat(wav);
        Wav.ReadMonoFloat(wav, out _); // JIT
        var sw = Stopwatch.StartNew();
        var samples = Wav.ReadMonoFloat(wav, out var rate);
        sw.Stop();
        _out.WriteLine($"ReadMonoFloat 30 s: {sw.Elapsed.TotalMilliseconds:F2} ms, {samples.Length} samples");
        Assert.Equal(Rate, rate);
        Assert.Equal(expected.Length, samples.Length);
        Assert.Equal(expected[0], samples[0], 1e-6);
        Assert.Equal(expected[expected.Length / 2], samples[expected.Length / 2], 1e-6);
        Assert.Equal(expected[^1], samples[^1], 1e-6);
        Assert.True(sw.ElapsedMilliseconds < 250, $"ReadMonoFloat 30 s took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void GeminiBody_WritesBase64Directly_SameJsonAsBefore()
    {
        var wav = Tone(seconds: 2, rmsDbfs: -30);
        GeminiClient.BuildTranscriptionBodyUtf8("gemini-3.1-flash-lite", wav, new[] { "Müller" }, null);
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var utf8 = GeminiClient.BuildTranscriptionBodyUtf8("gemini-3.1-flash-lite", wav, new[] { "Müller" }, null);
        sw.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"Gemini body 2 s WAV ({wav.Length} B): {sw.Elapsed.TotalMilliseconds:F2} ms, {alloc} bytes allocated, JSON {utf8.Length} B");

        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(utf8))!;
        Assert.Equal(Convert.ToBase64String(wav), (string)json["contents"]![0]!["parts"]![1]!["inline_data"]!["data"]!);
        Assert.Equal("audio/wav", (string)json["contents"]![0]!["parts"]![1]!["inline_data"]!["mime_type"]!);
        Assert.Contains("Müller", (string)json["contents"]![0]!["parts"]![0]!["text"]!);
        Assert.Equal(0.0, (double)json["generationConfig"]!["temperature"]!);
        // Writer buffer + ToArray, not a base64 string plus a JsonObject plus another string.
        Assert.True(alloc < utf8.Length * 3 + 64_000, $"Gemini body allocated {alloc} bytes for a {utf8.Length} byte JSON");
        Assert.True(sw.ElapsedMilliseconds < 100, $"Gemini body 2 s took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task GeminiAndOpenAi_DisableExpectContinue_AndPrewarmHitsOrigin()
    {
        var gh = new FakeHandler();
        gh.Responses.Enqueue((System.Net.HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"x\"}]}}]}"));
        var gemini = new GeminiClient(() => "k", () => "gemini-3.1-flash-lite", () => "gemini-3.8-flash", new HttpClient(gh));
        await gemini.TranscribeAsync(new byte[] { 1, 2, 3, 4 }, Array.Empty<string>());
        Assert.False(gh.Calls[0].Request.Headers.ExpectContinue);

        gh.Responses.Enqueue((System.Net.HttpStatusCode.NoContent, ""));
        await gemini.PrewarmConnection();
        Assert.Equal(HttpMethod.Head, gh.Calls[1].Request.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/", gh.Calls[1].Request.RequestUri!.ToString());

        var oh = new FakeHandler();
        oh.Responses.Enqueue((System.Net.HttpStatusCode.OK, "{\"text\":\"hi\"}"));
        var openAi = new OpenAiClient(() => "sk", () => "m", () => "m", new HttpClient(oh)) { RetryDelay = TimeSpan.Zero };
        await openAi.TranscribeAsync(new byte[] { 1, 2, 3, 4 }, Array.Empty<string>());
        Assert.False(oh.Calls[0].Request.Headers.ExpectContinue);

        oh.Responses.Enqueue((System.Net.HttpStatusCode.NoContent, ""));
        await openAi.PrewarmConnection();
        Assert.Equal(HttpMethod.Head, oh.Calls[1].Request.Method);
        Assert.Equal("https://api.openai.com/", oh.Calls[1].Request.RequestUri!.ToString());
    }

    // Old SpeechGate path: decode everything, then 30 ms RMS. Used as the oracle for the in-place scan.
    private static SpeechLevel ReferenceAnalyze(byte[] wav)
    {
        var samples = Wav.ReadMonoFloat(wav, out var rate);
        var frameLen = Math.Max(1, rate * 30 / 1000);
        var threshold = Math.Pow(10, SpeechGate.ThresholdDbfs / 20);
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
            if (Math.Sqrt(sum / frameLen) > threshold) loud++;
        }
        return new SpeechLevel(peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity, loud, frames);
    }

    // Per-sample BinaryPrimitives path, so the MemoryMarshal fast path cannot silently drift.
    private static float[] ReferenceReadMonoFloat(byte[] wav)
    {
        Assert.True(Wav.TryGetPcm16(wav, out var pcm));
        var frames = pcm.DataLength / pcm.FrameBytes;
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < pcm.Channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(pcm.DataOffset + (i * pcm.Channels + c) * 2, 2));
            samples[i] = sum / (float)pcm.Channels / 32768f;
        }
        return samples;
    }

    private static byte[] Tone(double seconds, double rmsDbfs, int channels = 1)
    {
        var amp = Math.Pow(10, rmsDbfs / 20) * Math.Sqrt(2);
        var frames = (int)Math.Round(Rate * seconds);
        var data = frames * channels * 2;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        for (var i = 0; i < frames; i++)
        {
            var v = (short)Math.Round(amp * Math.Sin(2 * Math.PI * 220 * i / Rate) * 32767);
            for (var c = 0; c < channels; c++)
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * channels + c) * 2), v);
        }
        return wav;
    }
}
