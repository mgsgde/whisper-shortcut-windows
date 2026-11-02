using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Tests;

public static class Paths
{
    public const string SwiftDir = "/Users/mgsgde/whisper-shortcut-private/whisper-shortcut/WhisperShortcut";
    public static string SwiftFile(string name) => Path.Combine(SwiftDir, name);

    public static string TestAudio(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "test-audio"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "test-audio", name);
    }
}

/// <summary>Skipped when the macOS Swift sources are not on this machine.</summary>
public sealed class SwiftSourceFactAttribute : FactAttribute
{
    public SwiftSourceFactAttribute()
    {
        if (!File.Exists(Paths.SwiftFile("AppConstants.swift"))) Skip = "Swift source not found";
    }
}

/// <summary>Skipped unless env GEMINI_API_KEY is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            Skip = "GEMINI_API_KEY not set";
    }
}

public sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
    public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        var (status, resp) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(resp, Encoding.UTF8, "application/json") };
    }

    public JsonNode Json(int i) => JsonNode.Parse(Calls[i].Body)!;
}

/// <summary>16-bit PCM WAV factory for speech-gate and pipeline tests.</summary>
public static class TestWav
{
    public const int Rate = 16000;

    /// <summary>Digital silence of <paramref name="ms"/> milliseconds.</summary>
    public static byte[] Silence(int ms) => Tone((ms, 0, 0));

    /// <summary>A 300 ms tone above the speech-gate threshold, padded with silence.</summary>
    public static byte[] Speech(int toneMs = 300) =>
        Tone((500, 0, 0), (toneMs, Math.Pow(10, -30.0 / 20) * Math.Sqrt(2), 220), (500, 0, 0));

    public static byte[] Tone(params (int Ms, double Amp, double Hz)[] segments)
    {
        var samples = new List<short>();
        foreach (var (ms, amp, hz) in segments)
        {
            var n = Rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var v = hz == 0 ? amp : amp * Math.Sin(2 * Math.PI * hz * i / Rate);
                samples.Add((short)Math.Round(v * 32767));
            }
        }
        var data = samples.Count * 2;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        for (var i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        return wav;
    }
}

/// <summary>Never answers; waits until the request token is cancelled (stall/timeout tests).</summary>
public sealed class HangHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }
}
