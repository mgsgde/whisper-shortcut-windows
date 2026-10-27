using System.Net;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class GeminiClientTests
{
    private static readonly byte[] Wav = { 1, 2, 3, 4 };
    private const string Answer = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hallo \"},{\"text\":\"Welt\"}]}}]}";

    private static (GeminiClient, FakeHandler) Make(string? key = "test-key")
    {
        var h = new FakeHandler();
        var c = new GeminiClient(() => key, () => "gemini-3.1-flash-lite", () => "gemini-3.8-flash", new HttpClient(h))
        { RetryDelay = TimeSpan.Zero, RetryInfoBuffer = TimeSpan.Zero };
        return (c, h);
    }

    [Fact]
    public async Task Transcribe_AnnotatedGlossary_SendsTieBreakerNotRejectedAsVocabulary()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.TranscribeAsync(Wav, new[] { "Bachata (not \"Batchata\")", "Ramipril" });

        var instruction = (string)h.Json(0)["contents"]![0]!["parts"]![0]!["text"]!;
        Assert.Contains(Prompts.GlossaryHintHeader + "Bachata, Ramipril", instruction);
        Assert.Contains("Homophone tie-breakers", instruction);
        Assert.DoesNotContain("(not", instruction);
    }

    [Fact]
    public async Task TranscribeShape_NoSystemInstruction_WithGlossary()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        var text = await c.TranscribeAsync(Wav, new[] { "Kubernetes", " Müller " });
        Assert.Equal("Hallo Welt", text);

        var req = h.Calls[0].Request;
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent",
            req.RequestUri!.ToString());
        Assert.Equal("test-key", req.Headers.GetValues("x-goog-api-key").Single());

        var j = h.Json(0);
        Assert.Null(j["systemInstruction"]);
        Assert.Null(j["contents"]![0]!["role"]);
        var parts = j["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal(2, parts.Count);
        var instruction = (string)parts[0]!["text"]!;
        Assert.StartsWith(Prompts.DefaultTranscriptionSystemPrompt + "\n\n" + Prompts.GlossaryHintHeader, instruction);
        Assert.EndsWith("Kubernetes, Müller", instruction);
        Assert.Equal("audio/wav", (string)parts[1]!["inline_data"]!["mime_type"]!);
        Assert.Equal(Convert.ToBase64String(Wav), (string)parts[1]!["inline_data"]!["data"]!);
        Assert.Equal("minimal", (string)j["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!);
    }

    [Fact]
    public async Task TranscribeWithoutGlossary_HasPlainInstruction()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.TranscribeAsync(Wav, Array.Empty<string>());
        Assert.Equal(Prompts.DefaultTranscriptionSystemPrompt, (string)h.Json(0)["contents"]![0]!["parts"]![0]!["text"]!);
    }

    [Fact]
    public async Task PromptWithSelection_Shape()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.PromptAsync(Wav, "Hallo Welt", null);

        Assert.EndsWith(":generateContent", h.Calls[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("gemini-3.8-flash", h.Calls[0].Request.RequestUri!.AbsolutePath);
        var j = h.Json(0);
        Assert.Equal(Prompts.DefaultPromptModeSystemPrompt + Prompts.PromptModeOutputRule,
            (string)j["systemInstruction"]!["parts"]![0]!["text"]!);
        Assert.Equal("user", (string)j["contents"]![0]!["role"]!);
        var parts = j["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal(Prompts.ClipboardSelectionHeader + "\n\nHallo Welt", (string)parts[0]!["text"]!);
        Assert.Equal("audio/wav", (string)parts[1]!["inline_data"]!["mime_type"]!);
        Assert.Equal("low", (string)j["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!);
    }

    [Fact]
    public async Task PromptText_Shape_NoAudio_SameSystemPromptAndThinking()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        var text = await c.PromptTextAsync("Hallo Welt", "Kürzer fassen", null);
        Assert.Equal("Hallo Welt", text);

        Assert.Contains("gemini-3.8-flash", h.Calls[0].Request.RequestUri!.AbsolutePath);
        var j = h.Json(0);
        Assert.Equal(Prompts.DefaultPromptModeSystemPrompt + Prompts.PromptModeOutputRule,
            (string)j["systemInstruction"]!["parts"]![0]!["text"]!);
        Assert.Equal("user", (string)j["contents"]![0]!["role"]!);
        var parts = j["contents"]![0]!["parts"]!.AsArray();
        Assert.Single(parts);
        Assert.Equal(DictatePromptText.LocalUserTurn("Hallo Welt", "Kürzer fassen"), (string)parts[0]!["text"]!);
        Assert.Equal("low", (string)j["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!);
    }

    [Fact]
    public async Task PromptWithoutSelection_UsesComposeMarker_AndCustomPrompt()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.PromptAsync(Wav, null, "  Mein Prompt  ");
        var j = h.Json(0);
        Assert.Equal("Mein Prompt" + Prompts.PromptModeOutputRule, (string)j["systemInstruction"]!["parts"]![0]!["text"]!);
        Assert.Equal(Prompts.DictatePromptComposeMarker, (string)j["contents"]![0]!["parts"]![0]!["text"]!);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[]}}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"role\":\"model\"}}]}")]
    [InlineData("not json")]
    public async Task EmptyResponse_ReturnsEmptyString(string body)
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, body));
        Assert.Equal("", await c.TranscribeAsync(Wav, Array.Empty<string>()));
        h.Responses.Enqueue((HttpStatusCode.OK, body));
        Assert.Equal("", await c.PromptAsync(Wav, "x", null));
    }

    [Fact]
    public async Task EmptyKey_ThrowsGermanMessage_WithoutNetwork()
    {
        var (c, h) = Make("");
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal("Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", ex.Message);
        Assert.Equal(GeminiErrorKind.NoKey, ex.Kind);
        Assert.Empty(h.Calls);
    }

    private const string RetryInfo0s =
        "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"0s\"}]}}";
    private const string PerMinute429 =
        "{\"error\":{\"code\":429,\"message\":\"Quota exceeded\",\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaId\":\"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"}]}]}}";
    private const string PerDay429 =
        "{\"error\":{\"code\":429,\"message\":\"Quota exceeded\",\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}]}]}}";

    /// <summary>Handler that runs a script per call (throw, cancel or answer).</summary>
    private sealed class ScriptHandler : HttpMessageHandler
    {
        public int Calls;
        public Func<int, CancellationToken, Task<HttpResponseMessage>> Script = (_, _) => throw new InvalidOperationException();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Script(++Calls, ct);
    }

    private static GeminiClient MakeScripted(ScriptHandler h) =>
        new(() => "k", () => "gemini-3.1-flash-lite", () => "gemini-3.8-flash", new HttpClient(h)) { RetryDelay = TimeSpan.Zero, RetryInfoBuffer = TimeSpan.Zero };

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Answer, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Temperature0_InTranscriptionBody_NotInPromptBodies()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.TranscribeAsync(Wav, Array.Empty<string>());
        await c.PromptAsync(Wav, "x", null);
        await c.PromptTextAsync("x", "y", null);
        Assert.Equal(0.0, (double)h.Json(0)["generationConfig"]!["temperature"]!);
        Assert.Null(h.Json(1)["generationConfig"]!["temperature"]);
        Assert.Null(h.Json(2)["generationConfig"]!["temperature"]);
    }

    [Fact]
    public async Task Retries_On503Twice_ThenSucceeds()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, "{}"));
        h.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, "{}"));
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        Assert.Equal("Hallo Welt", await c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(3, h.Calls.Count);
    }

    [Fact]
    public async Task ServerError_GetsSixAttempts()
    {
        var (c, h) = Make();
        for (var i = 0; i < 6; i++) h.Responses.Enqueue((HttpStatusCode.BadGateway, "{}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal(6, h.Calls.Count);
    }

    [Fact]
    public async Task Retries_NetworkException_ThenSucceeds()
    {
        var h = new ScriptHandler
        {
            Script = (n, _) => n == 1 ? throw new HttpRequestException("down") : Task.FromResult(Ok()),
        };
        Assert.Equal("Hallo Welt", await MakeScripted(h).TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task PersistentNetworkError_ThrowsNetwork_After5Attempts()
    {
        var h = new ScriptHandler { Script = (_, _) => throw new HttpRequestException("down") };
        var ex = await Assert.ThrowsAsync<GeminiException>(() => MakeScripted(h).TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
        Assert.Equal(5, h.Calls);
    }

    [Fact]
    public async Task Retries_RateLimit_WithRetryInfo0s()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.TooManyRequests, RetryInfo0s));
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        Assert.Equal("Hallo Welt", await c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task RateLimit_WithRetryInfoOver60s_StopsRetrying()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.TooManyRequests,
            "{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"120s\"}]}}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Single(h.Calls);
    }

    [Theory]
    [InlineData("55s", 55.0)]
    [InlineData("1.5s", 1.5)]
    [InlineData("0s", 0.0)]
    public void ParseRetryInfo_ReadsSeconds(string value, double seconds)
    {
        var body = "{\"error\":{\"details\":[{\"@type\":\"x.QuotaFailure\"},{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"" + value + "\"}]}}";
        Assert.Equal(TimeSpan.FromSeconds(seconds), GeminiClient.ParseRetryInfo(body));
        Assert.Null(GeminiClient.ParseRetryInfo("{}"));
        Assert.Null(GeminiClient.ParseRetryInfo("not json"));
    }

    [Fact]
    public async Task BadRequest_IsNotRetried()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad\"}}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Quota_After5Consecutive429_Throws()
    {
        var (c, h) = Make();
        for (var i = 0; i < 5; i++) h.Responses.Enqueue((HttpStatusCode.TooManyRequests, "{}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.PromptAsync(Wav, null, null));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal(5, h.Calls.Count);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var h = new ScriptHandler
        {
            Script = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Ok()); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeScripted(h).TranscribeAsync(Wav, Array.Empty<string>(), cts.Token));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task AttemptTimeout_IsRetried_ThenThrowsTimeout()
    {
        // Handler reports a cancelled linked token although the caller did not cancel = per-attempt timeout.
        var h = new ScriptHandler { Script = (_, _) => throw new OperationCanceledException() };
        var ex = await Assert.ThrowsAsync<GeminiException>(() => MakeScripted(h).TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Timeout, ex.Kind);
        Assert.Equal(5, h.Calls);
    }

    [Fact]
    public async Task Quota_PerMinute_SaysBusy_AndIsRetried()
    {
        var (c, h) = Make();
        for (var i = 0; i < 5; i++) h.Responses.Enqueue((HttpStatusCode.TooManyRequests, PerMinute429));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal("Gemini ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.", ex.Message);
        Assert.Equal(5, h.Calls.Count);
    }

    [Fact]
    public async Task Quota_PerDay_SaysExhausted_AndFailsAtOnce()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.TooManyRequests, PerDay429));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal("Kontingent erschöpft. Bitte später erneut versuchen oder das Google-Konto prüfen.", ex.Message);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task InvalidKey_On400ApiKeyInvalid_AndNoRetry()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"API key not valid.\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task KeyIsReadAtCallTime()
    {
        var h = new FakeHandler();
        string key = "";
        var c = new GeminiClient(() => key, () => "m", () => "m", new HttpClient(h));
        await Assert.ThrowsAsync<GeminiException>(() => c.TranscribeAsync(Wav, Array.Empty<string>()));
        key = "later";
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        await c.TranscribeAsync(Wav, Array.Empty<string>());
        Assert.Equal("later", h.Calls[0].Request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task Gemini38Transcription_ClampsMinimalToLow()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue((HttpStatusCode.OK, Answer));
        var c = new GeminiClient(() => "k", () => "gemini-3.8-flash", () => "x", new HttpClient(h));
        await c.TranscribeAsync(Wav, Array.Empty<string>());
        Assert.Equal("low", (string)h.Json(0)["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!);
    }

    // ---- live (skipped without GEMINI_API_KEY) ----

    private static GeminiClient Live() => new(
        () => Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
        () => Settings.DefaultTranscriptionModel, () => Settings.DefaultPromptModel);

    [LiveFact]
    public async Task Live_Transcribe_Diktat()
    {
        var wav = await File.ReadAllBytesAsync(Paths.TestAudio("diktat.wav"));
        var text = await Live().TranscribeAsync(wav, Array.Empty<string>());
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [LiveFact]
    public async Task Live_Prompt_AnweisungFreundlicher()
    {
        var wav = await File.ReadAllBytesAsync(Paths.TestAudio("anweisung-freundlicher.wav"));
        var text = await Live().PromptAsync(wav, "Schick mir das bis morgen.", null);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no network");
    }

    [Fact]
    public async Task CheckKey_Ok_SendsHeaderNotQueryKey()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, "{}"));
        Assert.Equal(KeyCheck.Ok, await c.CheckKeyAsync("secret-key"));
        var req = h.Calls.Single().Request;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("secret-key", req.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("key=", req.RequestUri!.ToString());
        Assert.DoesNotContain("secret-key", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckKey_400_IsInvalid()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"API key not valid\"}}"));
        Assert.Equal(KeyCheck.Invalid, await c.CheckKeyAsync("bad"));
    }

    [Fact]
    public async Task CheckKey_403_IsForbidden_NotInvalid()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.Forbidden, "{\"error\":{\"status\":\"PERMISSION_DENIED\"}}"));
        Assert.Equal(KeyCheck.Forbidden, await c.CheckKeyAsync("restricted"));
    }

    [Fact]
    public async Task CheckKey_NetworkError_IsNoConnection()
    {
        var c = new GeminiClient(() => null, () => "m", () => "m", new HttpClient(new ThrowingHandler()));
        Assert.Equal(KeyCheck.NoConnection, await c.CheckKeyAsync("k"));
    }

    [Fact]
    public async Task CheckKey_500_IsOther()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.InternalServerError, "{}"));
        Assert.Equal(KeyCheck.Other, await c.CheckKeyAsync("k"));
    }

    [Fact]
    public async Task CheckKey_Blank_IsInvalidWithoutRequest()
    {
        var (c, h) = Make();
        Assert.Equal(KeyCheck.Invalid, await c.CheckKeyAsync("  "));
        Assert.Empty(h.Calls);
    }
}
