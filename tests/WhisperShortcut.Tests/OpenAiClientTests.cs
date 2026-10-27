using System.Net;
using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class OpenAiClientTests
{
    private static readonly byte[] Wav = { 1, 2, 3, 4 };
    private const string Transcript = "{\"text\":\"Hallo Welt\"}";
    private const string Chat = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Fertig\"}}]}";

    private static (OpenAiClient, FakeHandler) Make(string? key = "sk-test")
    {
        var h = new FakeHandler();
        var c = new OpenAiClient(() => key, () => "gpt-4o-transcribe", () => "gpt-6-sol", new HttpClient(h))
        { RetryDelay = TimeSpan.Zero };
        return (c, h);
    }

    [Fact]
    public async Task Transcribe_Multipart_WithGlossaryPrompt_ReturnsText()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Transcript));
        var text = await c.TranscribeAsync(Wav, new[] { "Kubernetes", "Müller" });
        Assert.Equal("Hallo Welt", text);

        var req = h.Calls[0].Request;
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", req.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.MediaType);
        var body = h.Calls[0].Body;
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-4o-transcribe", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("name=file; filename=audio.wav", body);
        Assert.Contains("audio/wav", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("Kubernetes, Müller", body);
    }

    [Fact]
    public async Task Transcribe_NoGlossary_SendsNoPrompt_MissingTextIsEmpty()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, "{}"));
        Assert.Equal("", await c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.DoesNotContain("name=prompt", h.Calls[0].Body);
    }

    [Fact]
    public async Task PromptText_Shape()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Chat));
        Assert.Equal("Fertig", await c.PromptTextAsync("Hallo Welt", "kürzer", null));

        Assert.Equal("https://api.openai.com/v1/chat/completions", h.Calls[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", h.Calls[0].Request.Headers.Authorization!.ToString());
        var j = h.Json(0);
        Assert.Equal("gpt-6-sol", (string)j["model"]!);
        Assert.Equal("low", (string)j["reasoning_effort"]!);
        Assert.Equal(32000, (int)j["max_completion_tokens"]!);
        var m = j["messages"]!.AsArray();
        Assert.Equal(2, m.Count);
        Assert.Equal("system", (string)m[0]!["role"]!);
        Assert.Equal(DictatePromptText.SystemPrompt(null), (string)m[0]!["content"]!);
        Assert.Equal("user", (string)m[1]!["role"]!);
        Assert.Equal(DictatePromptText.LocalUserTurn("Hallo Welt", "kürzer"), (string)m[1]!["content"]!);
    }

    [Fact]
    public async Task PromptText_FinishLength_IsTruncated()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"abc\"}}]}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.Truncated, ex.Kind);
    }

    [Fact]
    public async Task Status401_IsInvalidKey_NoRetry()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_api_key\"}}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Equal("OpenAI-Schlüssel ungültig. Bitte in den Einstellungen prüfen.", ex.Message);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Status403_IsInvalidKey_NoRetry()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.Forbidden, "{}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Contains("Projekt- oder Organisationsrechte", ex.Message);
        Assert.Single(h.Calls);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"insufficient_quota\",\"type\":\"x\"}}")]
    [InlineData("{\"error\":{\"type\":\"insufficient_quota\"}}")]
    public async Task Status429_InsufficientQuota_FailsAtOnce(string body)
    {
        var (c, h) = Make();
        h.Responses.Enqueue(((HttpStatusCode)429, body));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Contains("Guthaben aufgebraucht", ex.Message);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Status429_RateLimit_RetriesThenSucceeds()
    {
        var (c, h) = Make();
        h.Responses.Enqueue(((HttpStatusCode)429, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}"));
        h.Responses.Enqueue((HttpStatusCode.OK, Chat));
        Assert.Equal("Fertig", await c.PromptTextAsync("x", "y", null));
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task Status503_ThenOk_RetriesAndRebuildsMultipart()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, "{}"));
        h.Responses.Enqueue((HttpStatusCode.OK, Transcript));
        Assert.Equal("Hallo Welt", await c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task Status500_GivesUpAfterSixAttempts_AsServer()
    {
        var (c, h) = Make();
        for (var i = 0; i < 10; i++) h.Responses.Enqueue((HttpStatusCode.InternalServerError, "{}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal("OpenAI-Fehler (HTTP 500). Bitte später erneut versuchen.", ex.Message);
        Assert.Equal(6, h.Calls.Count);
    }

    [Fact]
    public async Task Status429_RetryAfterOverSixtySeconds_FailsAtOnce()
    {
        var h = new HeaderHandler("retry-after", "120");
        var c = new OpenAiClient(() => "k", () => "m", () => "m", new HttpClient(h)) { RetryDelay = TimeSpan.Zero };
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Contains("ausgelastet", ex.Message);
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public async Task Status429_RetryAfterMs_IsHonoredAndRetried()
    {
        var h = new HeaderHandler("retry-after-ms", "5", failures: 1);
        var c = new OpenAiClient(() => "k", () => "m", () => "m", new HttpClient(h)) { RetryDelay = TimeSpan.Zero };
        Assert.Equal("Fertig", await c.PromptTextAsync("x", "y", null));
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public async Task MissingKey_IsNoKey_WithoutRequest()
    {
        var (c, h) = Make(key: " ");
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.NoKey, ex.Kind);
        Assert.Equal("Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", ex.Message);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task NetworkError_IsNetwork_AfterRetries()
    {
        var c = new OpenAiClient(() => "k", () => "m", () => "m", new HttpClient(new ThrowingHandler())) { RetryDelay = TimeSpan.Zero };
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptTextAsync("x", "y", null));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
        Assert.Contains("OpenAI", ex.Message);
    }

    [Fact]
    public async Task CheckKey_Ok_SendsBearerToModels()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, "{}"));
        Assert.Equal(KeyCheck.Ok, await c.CheckKeyAsync(" sk-abc "));
        var req = h.Calls.Single().Request;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.openai.com/v1/models", req.RequestUri!.ToString());
        Assert.Equal("Bearer sk-abc", req.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, KeyCheck.Invalid)]
    [InlineData(HttpStatusCode.Forbidden, KeyCheck.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, KeyCheck.Other)]
    [InlineData((HttpStatusCode)429, KeyCheck.Other)]
    public async Task CheckKey_MapsStatus(HttpStatusCode status, KeyCheck expected)
    {
        var (c, h) = Make();
        h.Responses.Enqueue((status, "{}"));
        Assert.Equal(expected, await c.CheckKeyAsync("k"));
    }

    [Fact]
    public async Task CheckKey_Blank_IsInvalidWithoutRequest()
    {
        var (c, h) = Make();
        Assert.Equal(KeyCheck.Invalid, await c.CheckKeyAsync("  "));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task CheckKey_NetworkError_IsNoConnection()
    {
        var c = new OpenAiClient(() => null, () => "m", () => "m", new HttpClient(new ThrowingHandler()));
        Assert.Equal(KeyCheck.NoConnection, await c.CheckKeyAsync("k"));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no network");
    }

    // Answers 429 (with one response header) for the first N calls (default: always), then a chat answer.
    private sealed class HeaderHandler : HttpMessageHandler
    {
        private readonly string _name, _value;
        private readonly int _failures;
        public int Count { get; private set; }

        public HeaderHandler(string name, string value, int failures = int.MaxValue)
        {
            _name = name; _value = value; _failures = failures;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            if (Count > _failures)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Chat, Encoding.UTF8, "application/json") });
            var r = new HttpResponseMessage((HttpStatusCode)429)
            { Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}", Encoding.UTF8, "application/json") };
            r.Headers.TryAddWithoutValidation(_name, _value);
            return Task.FromResult(r);
        }
    }
}
