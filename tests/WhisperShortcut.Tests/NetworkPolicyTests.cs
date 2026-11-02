using System.Net;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class NetworkPolicyTests
{
    [Theory]
    [InlineData("https://github.com/a/b.zip", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://127.0.0.1:8765/latest.json", true)]
    [InlineData("http://localhost/file", true)]
    [InlineData("http://example.com/file.zip", false)]
    [InlineData("https://example.com/file.zip", true)]
    [InlineData("file:///tmp/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void DownloadUrl_HttpsOrLoopback(string url, bool allowed) =>
        Assert.Equal(allowed, NetworkPolicy.IsAllowedDownloadUrl(url));

    [Theory]
    [InlineData("https://github.com/mgsgde/whisper-shortcut-windows/releases/latest", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("http://127.0.0.1/", false)]
    public void BrowserUrl_HttpsOnly(string url, bool allowed) =>
        Assert.Equal(allowed, NetworkPolicy.IsAllowedBrowserUrl(url));

    [Fact]
    public async Task SafeRedirect_StripsKeyOnCrossHostRedirect()
    {
        var inner = new ScriptHandler
        {
            Script = (n, req) => n == 1
                ? Redirect("https://evil.example/catch")
                : Ok(req),
        };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1/x");
        req.Headers.Add("x-goog-api-key", "secret-key");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "tok");
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls.Count);
        Assert.Equal("https://evil.example/catch", inner.Calls[1].RequestUri!.ToString());
        Assert.False(inner.Calls[1].Headers.Contains("x-goog-api-key"));
        Assert.Null(inner.Calls[1].Headers.Authorization);
    }

    [Fact]
    public async Task SafeRedirect_KeepsKeyOnSameHostRedirect()
    {
        var inner = new ScriptHandler
        {
            Script = (n, req) => n == 1
                ? Redirect("https://cdn.example/other.bin")
                : Ok(req),
        };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example/file.bin");
        req.Headers.Add("x-goog-api-key", "secret-key");
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("secret-key", inner.Calls[1].Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task SafeRedirect_RefusesHttpTargetAndInitialHttp()
    {
        var inner = new ScriptHandler { Script = (_, _) => Redirect("http://evil.example/x") };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://github.com/a"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("http://evil.example/x"));
    }

    [Fact]
    public async Task SafeRedirect_DoesNotFollowPost()
    {
        var inner = new ScriptHandler { Script = (_, req) => Redirect("https://evil.example/x") };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/x")
        {
            Content = new StringContent("body"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk");
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task SafeRedirect_AllowsLoopbackHttp()
    {
        var inner = new ScriptHandler { Script = (_, req) => Ok(req) };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        using var resp = await http.GetAsync("http://127.0.0.1:8765/latest.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task SafeRedirect_TooManyHops_Throws()
    {
        var inner = new ScriptHandler
        {
            Script = (n, _) => Redirect($"https://cdn.example/h{n}"),
        };
        using var http = new HttpClient(new SafeRedirectHandler(inner));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://cdn.example/start"));
        Assert.Equal(SafeRedirectHandler.MaxRedirects + 1, inner.Calls.Count);
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    private static HttpResponseMessage Ok(HttpRequestMessage req) =>
        new(HttpStatusCode.OK) { Content = new StringContent("ok"), RequestMessage = req };

    private sealed class ScriptHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = new();
        public Func<int, HttpRequestMessage, HttpResponseMessage> Script { get; set; } = (_, _) => new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(Script(Calls.Count, request));
        }
    }
}
