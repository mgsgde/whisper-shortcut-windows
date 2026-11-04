using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChatClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ChatMessage[] Conversation =
    {
        new(ChatRole.User, "Hallo", T0),
        new(ChatRole.Model, "Hi!", T0),
        new(ChatRole.User, "Wetter?", T0),
    };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _answer;
        private int _calls;
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
        public int CallCount => Volatile.Read(ref _calls);

        public Handler(Func<int, HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            int n;
            lock (Calls)
            {
                Calls.Add((request, body));
                n = ++_calls;
            }
            return await _answer(n, request, body, ct);
        }
    }

    private static ChatClient Client(Handler h, string? gemini = "g-key", string? openAi = "o-key") =>
        new(() => gemini, () => openAi, new HttpClient(h)) { RetryDelay = TimeSpan.Zero };

    private static Task<HttpResponseMessage> Sse(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    });

    private static Task<HttpResponseMessage> Status(HttpStatusCode code, string body = "{}") =>
        Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });

    private static string GeminiObject(string? text = null, bool thought = false, string[]? sources = null, string? finish = null)
    {
        var candidate = new JsonObject();
        if (text != null)
        {
            var part = new JsonObject { ["text"] = text };
            if (thought) part["thought"] = true;
            candidate["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part) };
        }
        if (sources != null)
        {
            var chunks = new JsonArray();
            foreach (var s in sources)
            {
                var (uri, title) = s.Split('|') is { Length: 2 } p ? (p[0], p[1]) : (s, "");
                chunks.Add(new JsonObject { ["web"] = new JsonObject { ["uri"] = uri, ["title"] = title } });
            }
            candidate["groundingMetadata"] = new JsonObject { ["groundingChunks"] = chunks };
        }
        if (finish != null) candidate["finishReason"] = finish;
        return "data: " + new JsonObject { ["candidates"] = new JsonArray(candidate) }.ToJsonString() + "\r\n\r\n";
    }

    private static string OpenAiObject(string? content, string? finish = null) =>
        "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["delta"] = content == null ? new JsonObject() : new JsonObject { ["content"] = content },
                ["finish_reason"] = finish,
            }),
        }.ToJsonString() + "\n\n";

    /// <summary>A body that yields <c>first</c>, then throws like a dropped connection.</summary>
    private sealed class DroppingStream(byte[] first) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent) throw new IOException("connection reset");
            _sent = true;
            first.CopyTo(buffer);
            return new ValueTask<int>(first.Length);
        }
    }

    private static Task<HttpResponseMessage> Dropping(string firstSse) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new DroppingStream(Encoding.UTF8.GetBytes(firstSse))),
    });

    private static Task<ChatReply> Gemini(ChatClient c, List<string>? deltas = null, bool webSearch = true, CancellationToken ct = default) =>
        c.SendAsync(Engine.Gemini, "gemini-3.8-flash", "SYSTEM", Conversation, deltas == null ? null : deltas.Add, webSearch, ct);

    private static Task<ChatReply> OpenAi(ChatClient c, List<string>? deltas = null, CancellationToken ct = default) =>
        c.SendAsync(Engine.OpenAI, "gpt-5", "SYSTEM", Conversation, deltas == null ? null : deltas.Add, false, ct);

    // ---- Gemini ----

    [Fact]
    public async Task Gemini_streams_text_skips_thoughts_and_collects_deduplicated_sources()
    {
        var h = new Handler((_, _, _, _) => Sse(
            GeminiObject("denke nach", thought: true)
            + GeminiObject("Heute ")
            + GeminiObject("ist es sonnig.", sources: new[] { "https://a.example|Seite A", "https://b.example|Seite B" })
            + GeminiObject(null, sources: new[] { "https://a.example|Seite A", "https://c.example|", }, finish: "STOP")));
        var deltas = new List<string>();
        var reply = await Gemini(Client(h), deltas);

        Assert.Equal("Heute ist es sonnig.", reply.Text);
        Assert.Equal(new[] { "Heute ", "ist es sonnig." }, deltas);
        Assert.Equal(new[] { "https://a.example", "https://b.example", "https://c.example" }, reply.Sources.Select(s => s.Url));
        Assert.Equal("Seite A", reply.Sources[0].Title);
        Assert.Equal("https://c.example", reply.Sources[2].Title); // no title: the URL stands in
    }

    [Fact]
    public async Task Gemini_request_has_url_key_system_roles_thinking_and_search_tool()
    {
        var h = new Handler((_, _, _, _) => Sse(GeminiObject("ok")));
        await Gemini(Client(h), webSearch: true);

        var (request, body) = h.Calls[0];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:streamGenerateContent?alt=sse",
            request.RequestUri!.ToString());
        Assert.Equal("g-key", request.Headers.GetValues("x-goog-api-key").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("SYSTEM", (string?)json["systemInstruction"]!["parts"]![0]!["text"]);
        var contents = json["contents"]!.AsArray();
        Assert.Equal(new[] { "user", "model", "user" }, contents.Select(c => (string?)c!["role"]));
        Assert.Equal("Wetter?", (string?)contents[2]!["parts"]![0]!["text"]);
        Assert.Equal("low", (string?)json["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]);
        Assert.NotNull(json["tools"]![0]!["google_search"]);
    }

    [Fact]
    public async Task Gemini_request_has_no_tools_without_web_search()
    {
        var h = new Handler((_, _, _, _) => Sse(GeminiObject("ok")));
        await Gemini(Client(h), webSearch: false);
        Assert.Null(JsonNode.Parse(h.Calls[0].Body)!["tools"]);
    }

    [Fact]
    public async Task Gemini_ignores_non_data_lines_and_garbage()
    {
        var h = new Handler((_, _, _, _) => Sse(": keep-alive\r\n\r\ndata: not json\r\n\r\n" + GeminiObject("Text") + "data: [DONE]\r\n"));
        Assert.Equal("Text", (await Gemini(Client(h))).Text);
    }

    [Fact]
    public async Task Gemini_empty_answer_throws_server_error_without_retry()
    {
        var h = new Handler((_, _, _, _) => Sse(GeminiObject(null, finish: "STOP")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h)));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Gemini_blocked_prompt_throws_server_error()
    {
        var h = new Handler((_, _, _, _) => Sse("data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}\r\n\r\n"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h)));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Contains("SAFETY", ex.Message);
        Assert.Equal(1, h.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GeminiErrorKind.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, GeminiErrorKind.InvalidKey)]
    [InlineData(HttpStatusCode.NotFound, GeminiErrorKind.Server)]
    public async Task Gemini_permanent_http_errors_are_mapped_and_not_retried(HttpStatusCode code, GeminiErrorKind kind)
    {
        var h = new Handler((_, _, _, _) => Status(code));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h)));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Gemini_daily_quota_is_not_retried()
    {
        var h = new Handler((_, _, _, _) => Status((HttpStatusCode)429, "{\"error\":{\"message\":\"PerDay limit\"}}"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h)));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    // ---- OpenAI ----

    [Fact]
    public async Task OpenAi_streams_deltas_and_stops_at_done()
    {
        var h = new Handler((_, _, _, _) => Sse(
            OpenAiObject("") + OpenAiObject("Guten ") + OpenAiObject("Tag") + OpenAiObject(null, "stop")
            + "data: [DONE]\n\n" + OpenAiObject("NACH DONE")));
        var deltas = new List<string>();
        var reply = await OpenAi(Client(h), deltas);

        Assert.Equal("Guten Tag", reply.Text);
        Assert.Equal(new[] { "Guten ", "Tag" }, deltas);
        Assert.Empty(reply.Sources);
    }

    [Fact]
    public async Task OpenAi_request_has_url_bearer_stream_and_roles()
    {
        var h = new Handler((_, _, _, _) => Sse(OpenAiObject("ok") + "data: [DONE]\n\n"));
        await OpenAi(Client(h));

        var (request, body) = h.Calls[0];
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("o-key", request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("gpt-5", (string?)json["model"]);
        Assert.True((bool)json["stream"]!);
        Assert.Null(json["tools"]);
        var messages = json["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => (string?)m!["role"]));
        Assert.Equal("SYSTEM", (string?)messages[0]!["content"]);
        Assert.Equal("Hi!", (string?)messages[2]!["content"]);
    }

    [Fact]
    public async Task OpenAi_empty_answer_throws_server_error()
    {
        var h = new Handler((_, _, _, _) => Sse(OpenAiObject(null, "stop") + "data: [DONE]\n\n"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h)));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal("OpenAI", ex.Provider);
    }

    [Fact]
    public async Task OpenAi_invalid_key_and_empty_account_are_mapped()
    {
        var h = new Handler((_, _, _, _) => Status(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h)));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);

        var h2 = new Handler((_, _, _, _) => Status((HttpStatusCode)429, "{\"error\":{\"code\":\"insufficient_quota\"}}"));
        var ex2 = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h2)));
        Assert.Equal(GeminiErrorKind.Billing, ex2.Kind);
        Assert.Equal(1, h2.CallCount);
    }

    // ---- retry ----

    [Fact]
    public async Task Retries_transient_failures_before_the_first_delta()
    {
        var h = new Handler((n, _, _, _) => n switch
        {
            1 => Status(HttpStatusCode.ServiceUnavailable),
            2 => Status((HttpStatusCode)429),
            _ => Sse(GeminiObject("geschafft")),
        });
        var reply = await Gemini(Client(h));
        Assert.Equal("geschafft", reply.Text);
        Assert.Equal(3, h.CallCount);
    }

    [Fact]
    public async Task Retries_network_errors_and_gives_up_after_two_extra_tries()
    {
        var h = new Handler((_, _, _, _) => throw new HttpRequestException("offline"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h)));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
        Assert.Equal(3, h.CallCount);
    }

    [Fact]
    public async Task Gives_up_with_the_mapped_error_when_the_server_keeps_failing()
    {
        var h = new Handler((_, _, _, _) => Status(HttpStatusCode.BadGateway));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h)));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal(3, h.CallCount);
    }

    [Fact]
    public async Task Does_not_retry_after_the_first_delta_was_delivered()
    {
        var h = new Handler((_, _, _, _) => Dropping(GeminiObject("Anfang")));
        var deltas = new List<string>();
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h), deltas));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
        Assert.Equal(1, h.CallCount);
        Assert.Equal(new[] { "Anfang" }, deltas);
    }

    [Fact]
    public async Task OpenAi_does_not_retry_after_the_first_delta_either()
    {
        var h = new Handler((_, _, _, _) => Dropping(OpenAiObject("Anfang")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h)));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task A_stream_that_drops_before_any_text_is_retried()
    {
        var h = new Handler((n, _, _, _) => n == 1 ? Dropping(": hello\r\n\r\n") : Sse(GeminiObject("zweiter Versuch")));
        Assert.Equal("zweiter Versuch", (await Gemini(Client(h))).Text);
        Assert.Equal(2, h.CallCount);
    }

    [Fact]
    public async Task A_silent_server_times_out_as_a_timeout_error()
    {
        var h = new Handler(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var client = Client(h);
        client.IdleTimeout = TimeSpan.FromMilliseconds(50);
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(client));
        Assert.Equal(GeminiErrorKind.Timeout, ex.Kind);
        Assert.Equal(3, h.CallCount);
    }

    // ---- keys, engine, cancellation ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_gemini_key_throws_NoKey_without_a_request(string? key)
    {
        var h = new Handler((_, _, _, _) => Sse(GeminiObject("x")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Gemini(Client(h, gemini: key)));
        Assert.Equal(GeminiErrorKind.NoKey, ex.Kind);
        Assert.Equal("Gemini", ex.Provider);
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task Missing_openai_key_throws_NoKey_without_a_request()
    {
        var h = new Handler((_, _, _, _) => Sse(OpenAiObject("x")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => OpenAi(Client(h, openAi: null)));
        Assert.Equal(GeminiErrorKind.NoKey, ex.Kind);
        Assert.Equal("OpenAI", ex.Provider);
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task Local_engine_is_refused()
    {
        var h = new Handler((_, _, _, _) => Sse(""));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(h).SendAsync(Engine.Local, "m", "s", Conversation, null, false, CancellationToken.None));
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceledException_without_retry()
    {
        using var cts = new CancellationTokenSource();
        var h = new Handler(async (_, _, _, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return null!; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gemini(Client(h), ct: cts.Token));
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Already_cancelled_token_sends_nothing()
    {
        var h = new Handler((_, _, _, _) => Sse(GeminiObject("x")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gemini(Client(h), ct: new CancellationToken(true)));
        Assert.Equal(0, h.CallCount);
    }
}
