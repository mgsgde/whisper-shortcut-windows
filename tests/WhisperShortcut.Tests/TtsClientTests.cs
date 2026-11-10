using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class TtsClientTests
{
    private static readonly TtsModel GeminiModel = ReadAloud.ModelById("gemini-3.8-flash-lite-tts");
    private static readonly TtsModel OpenAiModel = ReadAloud.ModelById("gpt-4o-mini-tts");

    /// <summary>Answers every request through a delegate; records request and body.</summary>
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

    private static TtsClient Client(Handler h, string? gemini = "g-key", string? openAi = "o-key") =>
        new(() => gemini, () => openAi, new HttpClient(h)) { RetryDelay = TimeSpan.Zero, RetryInfoBuffer = TimeSpan.Zero };

    private static byte[] Pcm(int bytes, byte value) => Enumerable.Repeat(value, bytes).ToArray();

    private static string SseObject(byte[] pcm, string? finish = null)
    {
        var o = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["inlineData"] = new JsonObject { ["mimeType"] = "audio/L16;codec=pcm;rate=24000", ["data"] = Convert.ToBase64String(pcm) },
                    }),
                },
            }),
        };
        if (finish != null) o["candidates"]![0]!["finishReason"] = finish;
        return o.ToJsonString();
    }

    private static string Sse(params byte[][] objects) =>
        string.Concat(objects.Select(o => $"data: {SseObject(o)}\r\n\r\n"));

    private static HttpResponseMessage Ok(string body, string mediaType = "text/event-stream") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static Task<HttpResponseMessage> Task_(HttpResponseMessage r) => Task.FromResult(r);

    // ---- request bodies ----

    [Fact]
    public void GeminiBody_HasTheStreamingShape_TextBare()
    {
        var j = JsonNode.Parse(TtsClient.BuildGeminiBody("Hallo \"Welt\" – äöü", "Charon"))!;
        Assert.Equal("Hallo \"Welt\" – äöü", (string)j["contents"]![0]!["parts"]![0]!["text"]!);
        Assert.Single(j["contents"]!.AsArray());
        Assert.Equal("AUDIO", (string)j["generationConfig"]!["responseModalities"]![0]!);
        Assert.Equal("Charon", (string)j["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!);
        Assert.Null(j["systemInstruction"]);
    }

    [Fact]
    public void OpenAiBody_AsksForRawPcm()
    {
        var j = JsonNode.Parse(TtsClient.BuildOpenAiBody("gpt-4o-mini-tts", "Hallo", "marin"))!;
        Assert.Equal("gpt-4o-mini-tts", (string)j["model"]!);
        Assert.Equal("Hallo", (string)j["input"]!);
        Assert.Equal("marin", (string)j["voice"]!);
        Assert.Equal("pcm", (string)j["response_format"]!);
    }

    // ---- stream object parsing ----

    [Fact]
    public void ParseStreamObject_JoinsAllInlineDataParts_AndReadsTheFinishReason()
    {
        var json = "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"data\":\"" + Convert.ToBase64String(new byte[] { 1, 2 })
            + "\"}},{\"text\":\"ignored\"},{\"inlineData\":{\"data\":\"" + Convert.ToBase64String(new byte[] { 3, 4 })
            + "\"}}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{}}";
        var (audio, finish) = TtsClient.ParseGeminiStreamObject(json);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, audio);
        Assert.Equal("STOP", finish);
    }

    [Theory]
    [InlineData("{\"usageMetadata\":{\"totalTokenCount\":5}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"STOP\"}]}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    public void ParseStreamObject_WithoutAudio_GivesNull(string json) =>
        Assert.Null(TtsClient.ParseGeminiStreamObject(json).Audio);

    [Fact]
    public void ParseStreamObject_ErrorObject_ThrowsMappedException()
    {
        var quota = Assert.Throws<GeminiException>(() => TtsClient.ParseGeminiStreamObject(
            "{\"error\":{\"code\":429,\"message\":\"slow down\",\"status\":\"RESOURCE_EXHAUSTED\"}}"));
        Assert.Equal(GeminiErrorKind.Quota, quota.Kind);
        var key = Assert.Throws<GeminiException>(() => TtsClient.ParseGeminiStreamObject(
            "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\"}}"));
        Assert.Equal(GeminiErrorKind.InvalidKey, key.Kind);
        var other = Assert.Throws<GeminiException>(() => TtsClient.ParseGeminiStreamObject("{\"error\":{\"message\":\"boom\"}}"));
        Assert.Equal(GeminiErrorKind.Server, other.Kind);
    }

    [Fact]
    public void ParseStreamObject_BlockedPrompt_Throws()
    {
        var ex = Assert.Throws<GeminiException>(() => TtsClient.ParseGeminiStreamObject("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"));
        Assert.Contains("SAFETY", ex.Message);
    }

    // ---- PCM batcher ----

    [Fact]
    public async Task Batcher_FirstSliceIs250ms_ThenHalfSeconds_NeverOdd()
    {
        var batcher = new PcmStreamBatcher();
        var slices = new List<byte[]>();
        Task Emit(byte[] s) { slices.Add(s); return Task.CompletedTask; }
        await batcher.AddAsync(new byte[5000], Emit);
        Assert.Empty(slices);
        await batcher.AddAsync(new byte[7001], Emit);
        Assert.Equal(new[] { 12000 }, slices.Select(s => s.Length));
        await batcher.AddAsync(new byte[50_000], Emit);
        Assert.Equal(new[] { 12000, 24000, 24000 }, slices.Select(s => s.Length));
        // Left: 1 + 50000 - 48000 = 2001 bytes; the odd byte at the end is dropped.
        var rest = batcher.Drain();
        Assert.Equal(2000, rest!.Length);
        Assert.Null(batcher.Drain());
    }

    // ---- one request ----

    [Fact]
    public async Task Gemini_SingleRequest_ShapeAndSseParsingOverSeveralDataLines()
    {
        var o1 = Pcm(10_000, 1);
        var o2 = Pcm(10_000, 2);
        var o3 = Pcm(10_000, 3);
        var h = new Handler((_, _, _, _) => Task_(Ok(
            ": comment line\r\n\r\n" + Sse(o1, o2, o3) + "data: {\"usageMetadata\":{}}\r\n\r\n" + "data: [DONE]\r\n\r\n")));
        var partials = new List<byte[]>();
        var pcm = await Client(h).SynthesizeChunkAsync(GeminiModel, "Kore", "Hallo Welt.", s => { partials.Add(s); return Task.CompletedTask; }, default);

        Assert.Equal(30_000, pcm.Length);
        Assert.Equal(o1.Concat(o2).Concat(o3).ToArray(), pcm);

        var req = h.Calls[0].Request;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash-lite-tts:streamGenerateContent?alt=sse",
            req.RequestUri!.ToString());
        Assert.Equal("g-key", req.Headers.GetValues("x-goog-api-key").Single());
        var j = JsonNode.Parse(h.Calls[0].Body)!;
        Assert.Equal("Hallo Welt.", (string)j["contents"]![0]!["parts"]![0]!["text"]!);
        Assert.Equal("Kore", (string)j["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!);

        // 30 000 bytes: first slice at 12 000, the remaining 18 000 are below the 24 000 batch and go out at the end.
        Assert.Equal(new[] { 12_000, 18_000 }, partials.Select(p => p.Length));
        Assert.Equal(pcm, partials.SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task Gemini_PartialsArriveInOrder_InBatches()
    {
        var objects = Enumerable.Range(1, 12).Select(i => Pcm(10_000, (byte)i)).ToArray();
        var h = new Handler((_, _, _, _) => Task_(Ok(Sse(objects))));
        var partials = new List<byte[]>();
        var pcm = await Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", s => { partials.Add(s); return Task.CompletedTask; }, default);
        Assert.Equal(120_000, pcm.Length);
        Assert.Equal(12_000, partials[0].Length);
        Assert.All(partials.Skip(1).Take(partials.Count - 2), p => Assert.Equal(24_000, p.Length));
        Assert.Equal(pcm, partials.SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task Gemini_WithoutPartialCallback_StillReturnsTheAudio()
    {
        var h = new Handler((_, _, _, _) => Task_(Ok(Sse(Pcm(2000, 7)))));
        var pcm = await Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default);
        Assert.Equal(Pcm(2000, 7), pcm);
    }

    [Fact]
    public async Task OpenAi_SingleRequest_ShapeAndBatchedBody()
    {
        var audio = Pcm(60_001, 5);
        var h = new Handler((_, _, _, _) => Task_(Ok("", "audio/pcm"))
            .ContinueWith(t => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) }));
        var partials = new List<byte[]>();
        var pcm = await Client(h).SynthesizeChunkAsync(OpenAiModel, "marin", "Hallo.", s => { partials.Add(s); return Task.CompletedTask; }, default);

        Assert.Equal(60_000, pcm.Length);   // the odd trailing byte is dropped
        var req = h.Calls[0].Request;
        Assert.Equal("https://api.openai.com/v1/audio/speech", req.RequestUri!.ToString());
        Assert.Equal("Bearer o-key", req.Headers.Authorization!.ToString());
        var j = JsonNode.Parse(h.Calls[0].Body)!;
        Assert.Equal("gpt-4o-mini-tts", (string)j["model"]!);
        Assert.Equal("Hallo.", (string)j["input"]!);
        Assert.Equal("marin", (string)j["voice"]!);
        Assert.Equal("pcm", (string)j["response_format"]!);

        Assert.Equal(new[] { 12_000, 24_000, 24_000 }, partials.Select(p => p.Length));
        Assert.Equal(pcm, partials.SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task MissingKey_ThrowsNoKey_WithoutARequest()
    {
        var h = new Handler((_, _, _, _) => Task_(Ok("")));
        var g = await Assert.ThrowsAsync<GeminiException>(() => Client(h, gemini: " ").SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(GeminiErrorKind.NoKey, g.Kind);
        Assert.Equal("Gemini", g.Provider);
        Assert.Equal("Kein Gemini-API-Schlüssel eingetragen. Bitte unter Einstellungen → Konten eintragen.", g.Message);

        var o = await Assert.ThrowsAsync<GeminiException>(() => Client(h, openAi: null).SynthesizeChunkAsync(OpenAiModel, "alloy", "x", null, default));
        Assert.Equal(GeminiErrorKind.NoKey, o.Kind);
        Assert.Equal("OpenAI", o.Provider);
        Assert.Equal("Kein OpenAI-API-Schlüssel eingetragen. Bitte unter Einstellungen → Konten eintragen.", o.Message);
        Assert.Equal(0, h.CallCount);
    }

    [Theory]
    [InlineData(401, GeminiErrorKind.InvalidKey)]
    [InlineData(429, GeminiErrorKind.Quota)]
    [InlineData(503, GeminiErrorKind.Server)]
    public async Task Gemini_ErrorStatus_MapsLikeGeminiClient(int status, GeminiErrorKind kind)
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("{\"error\":{\"status\":\"X\"}}") }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal("Gemini", ex.Provider);
    }

    [Fact]
    public async Task OpenAi_ErrorStatus_MapsLikeOpenAiClient()
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"error\":{\"code\":\"insufficient_quota\"}}") }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeChunkAsync(OpenAiModel, "alloy", "x", null, default));
        Assert.Equal(GeminiErrorKind.Billing, ex.Kind);
        Assert.Equal("OpenAI", ex.Provider);
    }

    [Fact]
    public async Task Gemini_2xxWithoutAudio_IsAServerError()
    {
        var h = new Handler((_, _, _, _) => Task_(Ok("data: {\"usageMetadata\":{}}\r\n\r\n")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
    }

    [Fact]
    public async Task NetworkFailure_IsMappedToNetwork()
    {
        var h = new Handler((_, _, _, _) => throw new HttpRequestException("offline"));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(GeminiErrorKind.Network, ex.Kind);
    }

    /// <summary>Delivers a first block, then hangs until the token is cancelled.</summary>
    private sealed class HangingStream : Stream
    {
        private readonly byte[] _first;
        private int _sent;
        public HangingStream(byte[] first) => _first = first;
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
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent < _first.Length)
            {
                var n = Math.Min(buffer.Length, _first.Length - _sent);
                _first.AsMemory(_sent, n).CopyTo(buffer);
                _sent += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    [Fact]
    public async Task Gemini_StalledStream_ThrowsTimeout()
    {
        var first = Encoding.UTF8.GetBytes($"data: {SseObject(Pcm(2000, 1))}\r\n\r\n");
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream(first)) }));
        var c = Client(h);
        c.StallTimeout = TimeSpan.FromMilliseconds(150);
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(GeminiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Gemini_NoAudioWithinTheFirstAudioBudget_ThrowsTimeout()
    {
        var h = new Handler(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(""); });
        var c = Client(h);
        c.FirstAudioTimeout = TimeSpan.FromMilliseconds(150);
        var ex = await Assert.ThrowsAsync<GeminiException>(() => c.SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, default));
        Assert.Equal(GeminiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Cancellation_IsNotATimeout()
    {
        var h = new Handler(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(""); });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).SynthesizeChunkAsync(GeminiModel, "Charon", "x", null, cts.Token));
    }

    // ---- whole text ----

    [Fact]
    public async Task Synthesize_RetriesOn503_ThenSucceeds()
    {
        var h = new Handler((n, _, _, _) => n == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") })
            : Task_(Ok(Sse(Pcm(3000, 9)))));
        var got = new List<byte>();
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", b => got.AddRange(b), default);
        Assert.Equal(0, failed);
        Assert.Equal(2, h.CallCount);
        Assert.Equal(Pcm(3000, 9), got.ToArray());
    }

    [Fact]
    public async Task Synthesize_DoesNotRetryAnInvalidKey_AndThrowsWhenAllChunksFailed()
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_GivesUpAfterTheMaxAttempts()
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal(HttpRetry.MaxAttempts, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_BlockedPrompt_IsNotRetried()
    {
        var h = new Handler((_, _, _, _) => Task_(Ok("data: {\"promptFeedback\":{\"blockReason\":\"OTHER\"}}\r\n\r\n")));
        await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(1, h.CallCount);
    }

    // ---- retry decision lives on the exception ----

    private static HttpResponseMessage Quota429(string body) =>
        new((HttpStatusCode)429) { Content = new StringContent(body) };

    [Fact]
    public async Task Synthesize_DoesNotRetryAGeminiDailyQuota()
    {
        // Used to retry 5 times with backoff (about 22 s per chunk) although the quota is gone until tomorrow.
        var h = new Handler((_, _, _, _) => Task.FromResult(Quota429(
            "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Quota exceeded for metric ... PerDay ...\"}}")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal("Kontingent erschöpft. Bitte später erneut versuchen oder das Google-Konto prüfen.", ex.Message);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_StillRetriesAPerMinuteRateLimit()
    {
        var h = new Handler((n, _, _, _) => n < 3
            ? Task.FromResult(Quota429("{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}}"))
            : Task_(Ok(Sse(Pcm(3000, 4)))));
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default);
        Assert.Equal(0, failed);
        Assert.Equal(3, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_HonoursRetryInfo_GivesUpWhenItAsksForMoreThanAMinute()
    {
        const string body = "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"100s\"}]}}";
        var h = new Handler((_, _, _, _) => Task.FromResult(Quota429(body)));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_HonoursRetryInfo_WaitsForAShortOneAndSucceeds()
    {
        const string body = "{\"error\":{\"code\":429,\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"0s\"}]}}";
        var h = new Handler((n, _, _, _) => n == 1 ? Task.FromResult(Quota429(body)) : Task_(Ok(Sse(Pcm(3000, 6)))));
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default);
        Assert.Equal(0, failed);
        Assert.Equal(2, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_OpenAi_HonoursRetryAfterHeader()
    {
        var h = new Handler((_, _, _, _) =>
        {
            var r = Quota429("{}");
            r.Headers.TryAddWithoutValidation("retry-after", "120");
            return Task.FromResult(r);
        });
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(OpenAiModel, "alloy", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_DoesNotRetryAnEmptyOpenAiAccount()
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(Quota429("{\"error\":{\"code\":\"insufficient_quota\"}}")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(OpenAiModel, "alloy", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Billing, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_RetriesA2xxWithoutAudio()
    {
        var h = new Handler((n, _, _, _) => n == 1 ? Task_(Ok("data: {\"usageMetadata\":{}}\r\n\r\n")) : Task_(Ok(Sse(Pcm(3000, 8)))));
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default);
        Assert.Equal(0, failed);
        Assert.Equal(2, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_DoesNotRetryAGoneModel()
    {
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Server, ex.Kind);
        Assert.Equal(1, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_InStreamQuotaErrorObject_FollowsTheSameRules()
    {
        var daily = new Handler((_, _, _, _) => Task_(Ok(
            "data: {\"error\":{\"code\":429,\"message\":\"Quota ... per day ...\"}}\r\n\r\n")));
        var ex = await Assert.ThrowsAsync<GeminiException>(() => Client(daily).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", _ => { }, default));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Equal(1, daily.CallCount);
    }

    private static string LongText() =>
        string.Join(" ", Enumerable.Range(1, 30).Select(i => $"Das ist Satz Nummer {i} und er ist ein wenig länger."));

    private static int ChunkIndexOf(string requestBody, IReadOnlyList<string> chunks)
    {
        var text = (string)JsonNode.Parse(requestBody)!["contents"]![0]!["parts"]![0]!["text"]!;
        return chunks.ToList().IndexOf(text);
    }

    [Fact]
    public async Task Synthesize_ManyChunks_AudioArrivesInPlaybackOrder_EvenWhenTheyFinishBackwards()
    {
        var text = LongText();
        var chunks = TtsTextChunker.Split(text, ReadAloud.ChunkSizeFor(TtsProvider.Gemini));
        Assert.True(chunks.Count >= 4);
        // The first chunk is the slowest, the last the fastest.
        var h = new Handler(async (_, _, body, ct) =>
        {
            var i = ChunkIndexOf(body, chunks);
            await Task.Delay((chunks.Count - i) * 40, ct);
            return Ok(Sse(Pcm(1000, (byte)(i + 1))));
        });
        var got = new List<byte>();
        var inCallback = 0;
        var overlapped = false;
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", text, b =>
        {
            if (Interlocked.Increment(ref inCallback) > 1) overlapped = true;
            got.AddRange(b);
            Interlocked.Decrement(ref inCallback);
        }, default);

        Assert.Equal(0, failed);
        Assert.False(overlapped);
        Assert.Equal(chunks.Count, h.CallCount);
        var expected = Enumerable.Range(0, chunks.Count).SelectMany(i => Pcm(1000, (byte)(i + 1))).ToArray();
        Assert.Equal(expected, got.ToArray());
    }

    [Fact]
    public async Task Synthesize_AChunkThatFailsForGood_IsSkipped_AndCounted()
    {
        var text = LongText();
        var chunks = TtsTextChunker.Split(text, ReadAloud.ChunkSizeFor(TtsProvider.Gemini));
        var h = new Handler((_, _, body, _) =>
        {
            var i = ChunkIndexOf(body, chunks);
            return i == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") })
                : Task_(Ok(Sse(Pcm(1000, (byte)(i + 1)))));
        });
        var got = new List<byte>();
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", text, b => got.AddRange(b), default);
        Assert.Equal(1, failed);
        var expected = Enumerable.Range(0, chunks.Count).Where(i => i != 1).SelectMany(i => Pcm(1000, (byte)(i + 1))).ToArray();
        Assert.Equal(expected, got.ToArray());
    }

    [Fact]
    public async Task Synthesize_Retry_EmitsOnlyTheBytesBeyondWhatAlreadyPlayed()
    {
        // First attempt: 30 000 bytes of 1s, then the stream breaks. Second attempt: the whole 40 000 bytes (value 2).
        var attempt1 = Encoding.UTF8.GetBytes(Sse(Pcm(10_000, 1), Pcm(10_000, 1), Pcm(10_000, 1)));
        var h = new Handler((n, _, _, _) =>
        {
            if (n == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(attempt1)) });
            return Task_(Ok(Sse(Pcm(10_000, 2), Pcm(10_000, 2), Pcm(10_000, 2), Pcm(10_000, 2))));
        });
        var got = new List<byte>();
        var failed = await Client(h).SynthesizeAsync(GeminiModel, "Charon", "Ein kurzer Satz.", b => got.AddRange(b), default);
        Assert.Equal(0, failed);
        Assert.Equal(2, h.CallCount);
        // The first attempt streamed 12 000 bytes live (first batch); the retry adds what lies beyond that.
        Assert.Equal(40_000, got.Count);
        Assert.Equal(Pcm(12_000, 1), got.Take(12_000).ToArray());
        Assert.Equal(Pcm(28_000, 2), got.Skip(12_000).ToArray());
    }

    /// <summary>Delivers its bytes, then fails like a dropped connection.</summary>
    private sealed class BrokenStream : Stream
    {
        private readonly MemoryStream _inner;
        public BrokenStream(byte[] data) => _inner = new MemoryStream(data);
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
            var n = _inner.Read(buffer.Span);
            if (n == 0) throw new IOException("connection reset");
            return new ValueTask<int>(n);
        }
    }

    [Fact]
    public async Task Synthesize_Cancellation_ThrowsOperationCanceled()
    {
        var h = new Handler(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(""); });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(h).SynthesizeAsync(GeminiModel, "Charon", LongText(), _ => { }, cts.Token));
    }

    [Fact]
    public async Task Synthesize_EmptyText_DoesNothing()
    {
        var h = new Handler((_, _, _, _) => Task_(Ok("")));
        Assert.Equal(0, await Client(h).SynthesizeAsync(GeminiModel, "Charon", "  ", _ => { }, default));
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task Synthesize_OpenAi_UsesTheLargerChunks()
    {
        var text = LongText();   // about 1500 chars
        var h = new Handler((_, _, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pcm(2000, 4)) }));
        var failed = await Client(h).SynthesizeAsync(OpenAiModel, "alloy", text, _ => { }, default);
        Assert.Equal(0, failed);
        Assert.Equal(TtsTextChunker.Split(text, ReadAloud.ChunkSizeChars).Count, h.CallCount);
        Assert.True(h.CallCount < TtsTextChunker.Split(text, ReadAloud.GeminiChunkSizeChars).Count);
    }

    // ---- PlaybackOrderGate ----

    private static (PlaybackOrderGate Gate, List<byte> Out) Gate(int total)
    {
        var list = new List<byte>();
        return (new PlaybackOrderGate(total, b => list.AddRange(b)), list);
    }

    [Fact]
    public void Gate_OutOfOrderCompletion_IsHeldUntilItsTurn()
    {
        var (gate, got) = Gate(3);
        gate.Complete(2, new byte[] { 30 });
        gate.Complete(1, new byte[] { 20 });
        Assert.Empty(got);
        gate.Complete(0, new byte[] { 10 });
        Assert.Equal(new byte[] { 10, 20, 30 }, got);
    }

    [Fact]
    public void Gate_PartialsOfTheHeadChunkPassThrough_ThenOnlyTheRemainderIsEmitted()
    {
        var (gate, got) = Gate(2);
        gate.Partial(0, new byte[] { 1, 2 });
        Assert.Equal(new byte[] { 1, 2 }, got);
        gate.Partial(0, new byte[] { 3, 4 });
        gate.Complete(0, new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, got);   // no byte twice
        gate.Complete(1, new byte[] { 7 });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, got);
    }

    [Fact]
    public void Gate_PartialsOfALaterChunk_AreBufferedUntilItsTurn_ThenStreamLive()
    {
        var (gate, got) = Gate(3);
        gate.Partial(1, new byte[] { 20, 21 });
        gate.Partial(2, new byte[] { 30 });
        Assert.Empty(got);
        gate.Complete(0, new byte[] { 10 });
        // Chunk 0 is out; chunk 1 is now at the head: what streamed in so far goes out, the rest follows live.
        Assert.Equal(new byte[] { 10, 20, 21 }, got);
        gate.Partial(1, new byte[] { 22 });
        Assert.Equal(new byte[] { 10, 20, 21, 22 }, got);
        gate.Complete(1, new byte[] { 20, 21, 22, 23 });
        // Chunk 2 is the head now: its buffered partial goes out, and only the rest of it follows on completion.
        Assert.Equal(new byte[] { 10, 20, 21, 22, 23, 30 }, got);
        gate.Complete(2, new byte[] { 30, 31 });
        Assert.Equal(new byte[] { 10, 20, 21, 22, 23, 30, 31 }, got);
    }

    [Fact]
    public void Gate_FailOpensTheGateForTheSuccessors()
    {
        var (gate, got) = Gate(3);
        gate.Complete(1, new byte[] { 20 });
        gate.Complete(2, new byte[] { 30 });
        Assert.Empty(got);
        gate.Fail(0);
        Assert.Equal(new byte[] { 20, 30 }, got);
    }

    [Fact]
    public void Gate_FailedChunk_IgnoresLatePartials()
    {
        var (gate, got) = Gate(2);
        gate.Fail(0);
        gate.Partial(0, new byte[] { 9 });
        gate.Complete(1, new byte[] { 20 });
        Assert.Equal(new byte[] { 20 }, got);
    }

    [Fact]
    public void Gate_Retry_EmitsOnlyTheBytesBeyondWhatAlreadyPlayed()
    {
        var (gate, got) = Gate(1);
        gate.Partial(0, new byte[] { 1, 2, 3 });
        gate.DiscardBuffered(0);
        gate.Complete(0, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, got);
    }

    [Fact]
    public void Gate_DiscardBuffered_DropsPartialsThatNeverPlayed()
    {
        var (gate, got) = Gate(2);
        gate.Partial(1, new byte[] { 99, 99 });   // buffered, not at the head
        gate.DiscardBuffered(1);
        gate.Complete(0, new byte[] { 1 });
        Assert.Equal(new byte[] { 1 }, got);       // chunk 1 has nothing buffered any more
        gate.Complete(1, new byte[] { 20, 21 });
        Assert.Equal(new byte[] { 1, 20, 21 }, got);
    }

    [Fact]
    public async Task Gate_ConcurrentUse_NeverOverlapsEmits()
    {
        var overlapped = false;
        var inside = 0;
        var total = 0;
        var gate = new PlaybackOrderGate(50, b =>
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            total += b.Length;
            Interlocked.Decrement(ref inside);
        });
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            gate.Partial(i, new byte[] { 1, 2 });
            gate.Complete(i, new byte[] { 1, 2, 3 });
        })));
        Assert.False(overlapped);
        Assert.Equal(150, total);
    }
}
