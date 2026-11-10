using System.Net;
using System.Text.Json;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>The wire profile of both providers: the German texts every client shows, auth, status mapping and retry flags.</summary>
public class CloudApiTests
{
    [Fact]
    public void NoKey_TextsPerPlace_StayAsTheyWere()
    {
        Assert.Equal("Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", CloudApi.Gemini.NoKey(KeyHint.Settings).Message);
        Assert.Equal("Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", CloudApi.OpenAi.NoKey(KeyHint.Settings).Message);
        Assert.Equal("Kein Gemini-API-Schlüssel eingetragen. Bitte unter Einstellungen → Konten eintragen.", CloudApi.Gemini.NoKey(KeyHint.Accounts).Message);
        Assert.Equal("Kein OpenAI-API-Schlüssel eingetragen. Bitte unter Einstellungen → Konten eintragen.", CloudApi.OpenAi.NoKey(KeyHint.Accounts).Message);
        Assert.All(new[] { CloudApi.Gemini, CloudApi.OpenAi }, api =>
        {
            var ex = api.NoKey(KeyHint.Settings);
            Assert.Equal(GeminiErrorKind.NoKey, ex.Kind);
            Assert.Equal(api.Name, ex.Provider);
            Assert.False(ex.Retryable);
        });
    }

    [Fact]
    public async Task NoKey_InChat_UsesTheSettingsWording()
    {
        var chat = new ChatClient(() => null, () => " ");
        var g = await Assert.ThrowsAsync<GeminiException>(() => chat.SendAsync(Engine.Gemini, "m", "s", Array.Empty<ChatMessage>(), null, false, default));
        var o = await Assert.ThrowsAsync<GeminiException>(() => chat.SendAsync(Engine.OpenAI, "m", "s", Array.Empty<ChatMessage>(), null, false, default));
        Assert.Equal("Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", g.Message);
        Assert.Equal("Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.", o.Message);
        Assert.Equal("Gemini", g.Provider);
        Assert.Equal("OpenAI", o.Provider);
    }

    [Fact]
    public void TimeoutAndNetwork_TextsAndRetryFlags()
    {
        Assert.Equal("Zeitüberschreitung: Gemini hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.", CloudApi.Gemini.Timeout().Message);
        Assert.Equal("Zeitüberschreitung: OpenAI hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.", CloudApi.OpenAi.Timeout().Message);
        var inner = new HttpRequestException("offline");
        var g = CloudApi.Gemini.Network(inner);
        Assert.Equal("Kein Netz: Gemini ist nicht erreichbar. Bitte Internetverbindung prüfen.", g.Message);
        Assert.Equal("Kein Netz: OpenAI ist nicht erreichbar. Bitte Internetverbindung prüfen.", CloudApi.OpenAi.Network(inner).Message);
        Assert.Same(inner, g.InnerException);
        Assert.Equal(GeminiErrorKind.Network, g.Kind);
        Assert.Equal(GeminiErrorKind.Timeout, CloudApi.OpenAi.Timeout().Kind);
        Assert.True(g.Retryable);
        Assert.True(CloudApi.OpenAi.Timeout().Retryable);
        Assert.False(g.ServerFault);
    }

    [Fact]
    public void Authorize_PutsTheKeyInTheProvidersHeader_NeverInTheUrl()
    {
        using var g = new HttpRequestMessage(HttpMethod.Post, "https://example.test/x");
        CloudApi.Gemini.Authorize(g, "g-key");
        Assert.Equal("g-key", g.Headers.GetValues("x-goog-api-key").Single());
        Assert.Null(g.Headers.Authorization);
        using var o = new HttpRequestMessage(HttpMethod.Post, "https://example.test/x");
        CloudApi.OpenAi.Authorize(o, "o-key");
        Assert.Equal("Bearer o-key", o.Headers.Authorization!.ToString());
        Assert.False(o.Headers.Contains("x-goog-api-key"));
    }

    [Fact]
    public void Bases_AreThePublicEndpoints()
    {
        Assert.Equal("https://generativelanguage.googleapis.com/", CloudApi.Gemini.Origin);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/", CloudApi.Gemini.ApiBase);
        Assert.Equal("https://api.openai.com/", CloudApi.OpenAi.Origin);
        Assert.Equal("https://api.openai.com/v1/", CloudApi.OpenAi.ApiBase);
    }

    [Theory]
    [InlineData(401, "{}", GeminiErrorKind.InvalidKey, "API-Schlüssel ungültig. Bitte in den Einstellungen prüfen.")]
    [InlineData(403, "{}", GeminiErrorKind.InvalidKey, "API-Schlüssel ungültig. Bitte in den Einstellungen prüfen.")]
    [InlineData(429, "{}", GeminiErrorKind.Quota, "Gemini ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.")]
    [InlineData(429, "{\"error\":{\"message\":\"Quota exceeded for ...PerDay...\"}}", GeminiErrorKind.Quota, "Kontingent erschöpft. Bitte später erneut versuchen oder das Google-Konto prüfen.")]
    public void Gemini_StatusMapping_Texts(int status, string body, GeminiErrorKind kind, string message)
    {
        var ex = CloudApi.Gemini.MapHttpError((HttpStatusCode)status, body, null);
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(message, ex.Message);
        Assert.Equal("Gemini", ex.Provider);
    }

    [Theory]
    [InlineData(401, "", GeminiErrorKind.InvalidKey, "OpenAI-Schlüssel ungültig. Bitte in den Einstellungen prüfen.")]
    [InlineData(403, "", GeminiErrorKind.InvalidKey, "Dieser OpenAI-Schlüssel darf das Modell nicht benutzen (Projekt- oder Organisationsrechte prüfen).")]
    [InlineData(429, "{\"error\":{\"type\":\"insufficient_quota\"}}", GeminiErrorKind.Billing, "Das OpenAI-Konto hat kein Guthaben mehr.")]
    [InlineData(429, "{}", GeminiErrorKind.Quota, "OpenAI ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.")]
    public void OpenAi_StatusMapping_Texts(int status, string body, GeminiErrorKind kind, string message)
    {
        var ex = CloudApi.OpenAi.MapHttpError((HttpStatusCode)status, body, null);
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(message, ex.Message);
        Assert.Equal("OpenAI", ex.Provider);
    }

    [Fact]
    public void ModelGoneAndServerBusy_UseTheSharedCopy()
    {
        Assert.Equal(UserCopy.ModelGone("Gemini"), CloudApi.Gemini.MapHttpError(HttpStatusCode.NotFound, "{}", null).Message);
        Assert.Equal(UserCopy.ServerBusy("Gemini"), CloudApi.Gemini.MapHttpError(HttpStatusCode.BadGateway, "{}", null).Message);
        Assert.Equal(UserCopy.ModelGone("OpenAI"), CloudApi.OpenAi.MapHttpError(HttpStatusCode.NotFound, "{}", null).Message);
        Assert.Equal(UserCopy.ServerBusy("OpenAI"), CloudApi.OpenAi.MapHttpError(HttpStatusCode.InternalServerError, "{}", null).Message);
    }

    [Theory]
    [InlineData(429, "{}", true, false)]
    [InlineData(429, "{\"error\":{\"message\":\"... per day ...\"}}", false, false)]
    [InlineData(500, "{}", true, true)]
    [InlineData(503, "{}", true, true)]
    [InlineData(404, "{}", false, false)]
    [InlineData(400, "{}", false, false)]
    [InlineData(401, "{}", false, false)]
    public void Gemini_RetryFlags(int status, string body, bool retryable, bool serverFault)
    {
        var ex = CloudApi.Gemini.MapHttpError((HttpStatusCode)status, body, null);
        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(serverFault, ex.ServerFault);
    }

    [Fact]
    public void OpenAi_RetryFlags_EmptyAccountIsNotRetried_RetryAfterIsRead()
    {
        Assert.False(CloudApi.OpenAi.MapHttpError(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"insufficient_quota\"}}", null).Retryable);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("retry-after", "7");
        var ex = CloudApi.OpenAi.MapHttpError(HttpStatusCode.TooManyRequests, "{}", response.Headers);
        Assert.True(ex.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Null(CloudApi.OpenAi.MapHttpError(HttpStatusCode.ServiceUnavailable, "{}", response.Headers).RetryAfter);
    }

    [Fact]
    public void Gemini_RetryAfter_ComesFromRetryInfoOn429()
    {
        const string body = "{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"12s\"}]}}";
        Assert.Equal(TimeSpan.FromSeconds(12), CloudApi.Gemini.MapHttpError(HttpStatusCode.TooManyRequests, body, null).RetryAfter);
        Assert.Null(CloudApi.Gemini.MapHttpError(HttpStatusCode.ServiceUnavailable, body, null).RetryAfter);
    }

    [Fact]
    public void LogStatus_IsOnlyTheStatusWord()
    {
        Assert.Equal("RESOURCE_EXHAUSTED", CloudApi.Gemini.LogStatus("{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"secret prompt\"}}"));
        Assert.Equal("", CloudApi.Gemini.LogStatus("nope"));
        Assert.Equal("rate_limit_exceeded", CloudApi.OpenAi.LogStatus("{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"secret prompt\"}}"));
        Assert.Equal("insufficient_quota", CloudApi.OpenAi.LogStatus("{\"error\":{\"code\":\"x\",\"type\":\"insufficient_quota\"}}"));
    }

    private static GeminiException? Stream(CloudApi api, string json, string what = "die Anfrage")
    {
        using var doc = JsonDocument.Parse(json);
        return api.StreamError(doc.RootElement.Clone(), json, "Test", what);
    }

    [Fact]
    public void StreamError_Gemini_ErrorObjectAndBlockedPrompt()
    {
        var quota = Stream(CloudApi.Gemini, "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}}")!;
        Assert.Equal(GeminiErrorKind.Quota, quota.Kind);
        Assert.True(quota.Retryable);
        var odd = Stream(CloudApi.Gemini, "{\"error\":{\"code\":200}}")!;
        Assert.Equal(GeminiErrorKind.Server, odd.Kind);
        Assert.True(odd.Retryable);   // an out-of-range code counts as 500
        var chat = Stream(CloudApi.Gemini, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")!;
        Assert.Equal("Gemini hat die Anfrage abgelehnt (SAFETY).", chat.Message);
        Assert.False(chat.Retryable);
        var tts = Stream(CloudApi.Gemini, "{\"promptFeedback\":{\"blockReason\":\"OTHER\"}}", "den Text zum Vorlesen")!;
        Assert.Equal("Gemini hat den Text zum Vorlesen abgelehnt (OTHER).", tts.Message);
        Assert.False(tts.Retryable);
        Assert.Null(Stream(CloudApi.Gemini, "{\"candidates\":[]}"));
        Assert.Null(Stream(CloudApi.Gemini, "{\"error\":null}"));
    }

    [Fact]
    public void StreamError_OpenAi_OnlyAnErrorObject()
    {
        var ex = Stream(CloudApi.OpenAi, "{\"error\":{\"message\":\"boom\"}}")!;
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal("OpenAI", ex.Provider);
        Assert.True(ex.Retryable);
        Assert.Null(Stream(CloudApi.OpenAi, "{\"error\":\"text\"}"));
        Assert.Null(Stream(CloudApi.OpenAi, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    [Fact]
    public async Task CloudHttp_Send_SetsAuthAndNoExpectContinue_AndMapsNon2xx()
    {
        HttpRequestMessage? seen = null;
        var http = new HttpClient(new Handler(r =>
        {
            seen = r;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => CloudHttp.SendAsync(http, CloudApi.OpenAi, "https://example.test/x", "k",
            new StringContent("{}"), "T", default, default, _ => Task.FromResult(1)));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Equal("Bearer k", seen!.Headers.Authorization!.ToString());
        Assert.False(seen.Headers.ExpectContinue ?? false);
    }

    [Fact]
    public async Task CloudHttp_Send_MapsTransportErrors_ButNotTheCallersCancel()
    {
        var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        var net = await Assert.ThrowsAsync<GeminiException>(() => CloudHttp.SendAsync(offline, CloudApi.Gemini, "https://example.test/x", "k",
            new StringContent("{}"), "T", default, default, _ => Task.FromResult(1)));
        Assert.Equal(GeminiErrorKind.Network, net.Kind);

        using var watchdog = new CancellationTokenSource(50);
        var slow = new HttpClient(new Handler(async _ => { await Task.Delay(Timeout.Infinite, watchdog.Token); return null!; }));
        var timeout = await Assert.ThrowsAsync<GeminiException>(() => CloudHttp.SendAsync(slow, CloudApi.Gemini, "https://example.test/x", "k",
            new StringContent("{}"), "T", watchdog.Token, default, _ => Task.FromResult(1)));
        Assert.Equal(GeminiErrorKind.Timeout, timeout.Kind);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudHttp.SendAsync(slow, CloudApi.Gemini, "https://example.test/x", "k",
            new StringContent("{}"), "T", cancelled.Token, cancelled.Token, _ => Task.FromResult(1)));
    }

    [Fact]
    public async Task CheckKey_UsesTheProvidersListEndpointAndStatusTable()
    {
        var urls = new List<string>();
        HttpClient Answering(HttpStatusCode code) => new(new Handler(r =>
        {
            urls.Add(r.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(code));
        }));
        Assert.Equal(KeyCheck.Invalid, await CloudApi.Gemini.CheckKeyAsync(Answering(HttpStatusCode.BadRequest), "k", default));
        Assert.Equal(KeyCheck.Other, await CloudApi.OpenAi.CheckKeyAsync(Answering(HttpStatusCode.BadRequest), "k", default));
        Assert.Equal(KeyCheck.Forbidden, await CloudApi.OpenAi.CheckKeyAsync(Answering(HttpStatusCode.Forbidden), "k", default));
        Assert.Equal(KeyCheck.Invalid, await CloudApi.Gemini.CheckKeyAsync(Answering(HttpStatusCode.OK), " ", default));
        Assert.Equal(new[]
        {
            "https://generativelanguage.googleapis.com/v1beta/models/?pageSize=1",
            "https://api.openai.com/v1/models",
            "https://api.openai.com/v1/models",
        }, urls);
    }
}
