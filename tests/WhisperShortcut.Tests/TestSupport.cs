using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
