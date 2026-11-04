using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core;

/// <summary>A finished chat answer: the full text and the web sources it was grounded on (Gemini only).</summary>
public sealed record ChatReply(string Text, IReadOnlyList<ChatSource> Sources);

/// <summary>
/// Streaming chat against Gemini (streamGenerateContent over SSE, with Google Search grounding) and OpenAI
/// (chat completions with <c>stream: true</c>). Keys are read at call time. Transient failures are retried only
/// while nothing has reached the caller yet: once a piece of text was delivered, an error ends the answer.
/// </summary>
public sealed class ChatClient
{
    private const string GeminiBase = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string OpenAiUrl = "https://api.openai.com/v1/chat/completions";

    /// <summary>Extra tries after the first one.</summary>
    public const int MaxRetries = 2;

    // Exception.Data marker: the failure came from a transient HTTP status or the network.
    private const string TransientKey = "chat.transient";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _geminiKey;
    private readonly Func<string?> _openAiKey;
    private readonly HttpClient _http;

    /// <summary>Base of the backoff between retries (1.5 s, doubling; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long the server may stay silent (before the first byte and between stream lines) before the request
    /// counts as timed out. Search grounding can think for a while before the first token.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Creates a client with key providers (evaluated on every call).</summary>
    public ChatClient(Func<string?> geminiKey, Func<string?> openAiKey, HttpClient? http = null)
    {
        _geminiKey = geminiKey;
        _openAiKey = openAiKey;
        _http = http ?? NetworkPolicy.CreateApiClient();
    }

    /// <summary>
    /// Sends the conversation and streams the answer. <paramref name="onDelta"/> gets each text piece as it arrives
    /// (on the HTTP thread). <paramref name="webSearch"/> turns on Google Search grounding (Gemini only).
    /// Throws <see cref="GeminiException"/> for key, HTTP, network and empty-answer problems,
    /// <see cref="OperationCanceledException"/> on cancel, and <see cref="ArgumentException"/> for <see cref="Engine.Local"/>.
    /// </summary>
    public async Task<ChatReply> SendAsync(Engine engine, string model, string systemPrompt,
        IReadOnlyList<ChatMessage> messages, Action<string>? onDelta, bool webSearch, CancellationToken ct)
    {
        if (engine == Engine.Local)
            throw new ArgumentException("Der Offline-Motor wird von der App selbst bedient.", nameof(engine));
        var gemini = engine == Engine.Gemini;
        var provider = gemini ? "Gemini" : "OpenAI";
        var key = (gemini ? _geminiKey() : _openAiKey())?.Trim();
        if (string.IsNullOrEmpty(key))
            throw gemini
                ? new GeminiException(GeminiErrorKind.NoKey, "Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.")
                : new GeminiException("OpenAI", GeminiErrorKind.NoKey, "Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.");

        var delivered = false;
        void Deliver(string piece)
        {
            if (piece.Length == 0) return;
            delivered = true;
            onDelta?.Invoke(piece);
        }

        var body = gemini
            ? BuildGeminiBody(systemPrompt, messages, webSearch)
            : BuildOpenAiBody(model, systemPrompt, messages);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            GeminiException failure;
            try
            {
                return gemini
                    ? await StreamGeminiAsync(model, key, body, Deliver, ct).ConfigureAwait(false)
                    : await StreamOpenAiAsync(model, key, body, Deliver, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (GeminiException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException)
            {
                Log.Error($"Chat {provider} {model}: stalled");
                failure = new GeminiException(provider, GeminiErrorKind.Timeout,
                    $"Zeitüberschreitung: {provider} hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Log.Error($"Chat {provider} {model}: network", ex);
                failure = new GeminiException(provider, GeminiErrorKind.Network,
                    $"Kein Netz: {provider} ist nicht erreichbar. Bitte Internetverbindung prüfen.", ex);
            }

            var transient = failure.Kind is GeminiErrorKind.Network or GeminiErrorKind.Timeout || failure.Data.Contains(TransientKey);
            if (delivered || !transient || attempt > MaxRetries) throw failure;
            var delay = HttpRetry.Backoff(RetryDelay, attempt);
            Log.Info($"Chat {provider} {model}: attempt {attempt} failed ({failure.Kind}), retrying in {delay.TotalSeconds:0.#} s");
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    // ---- request bodies ----

    internal static string BuildGeminiBody(string systemPrompt, IReadOnlyList<ChatMessage> messages, bool webSearch)
    {
        var contents = new JsonArray();
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue; // Gemini rejects empty parts
            contents.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "model",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }),
            });
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }),
            },
            ["contents"] = contents,
            // "low", not "minimal": the 3.8 models reject "minimal".
            ["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" },
            },
        };
        if (webSearch)
            body["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });
        return body.ToJsonString(Compact);
    }

    internal static string BuildOpenAiBody(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            list.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            list.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "assistant",
                ["content"] = m.Text,
            });
        }
        return new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = list }.ToJsonString(Compact);
    }

    // ---- Gemini ----

    private async Task<ChatReply> StreamGeminiAsync(string model, string key, string body, Action<string> deliver, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(IdleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GeminiBase}{model}:streamGenerateContent?alt=sse")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = false;
        request.Headers.Add("x-goog-api-key", key);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(watchdog.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            Log.Error($"Chat Gemini {model}: HTTP {code} {GeminiClient.GoogleStatus(text)}");
            throw Mark(GeminiClient.MapStatus(response.StatusCode, text), code, IsDaily(text));
        }

        var reply = new StringBuilder();
        var sources = new List<ChatSource>();
        using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(watchdog.Token).ConfigureAwait(false) is { } line)
        {
            watchdog.CancelAfter(IdleTimeout);
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line.Substring(5).Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            var piece = ParseGeminiChunk(json, sources);
            if (piece.Length == 0) continue;
            reply.Append(piece);
            deliver(piece);
        }
        if (reply.Length == 0)
        {
            Log.Error($"Chat Gemini {model}: 2xx without any text");
            throw new GeminiException(GeminiErrorKind.Server, "Gemini hat keine Antwort geliefert. Bitte noch einmal versuchen.");
        }
        Log.Info($"Chat Gemini {model}: {reply.Length} chars, {sources.Count} sources");
        return new ChatReply(reply.ToString(), sources);
    }

    /// <summary>
    /// One SSE object: the visible text (thought parts skipped; "" when none). New grounding sources are added to
    /// <paramref name="sources"/>, de-duplicated by URL. A top-level error or a blocked prompt throws.
    /// Unparseable JSON counts as an empty chunk.
    /// </summary>
    internal static string ParseGeminiChunk(string json, List<ChatSource> sources)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return ""; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                // The HTTP status was 200, so the code lives in the error object.
                var code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c)
                    && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n is >= 400 and <= 599 ? n : 500;
                Log.Error($"Chat Gemini: stream error object {code} {GeminiClient.GoogleStatus(json)}");
                throw Mark(GeminiClient.MapStatus((HttpStatusCode)code, json), code, IsDaily(json));
            }
            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object
                && feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String)
                throw new GeminiException(GeminiErrorKind.Server, $"Gemini hat die Anfrage abgelehnt ({block.GetString()}).");

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0 || candidates[0].ValueKind != JsonValueKind.Object) return "";
            var candidate = candidates[0];

            if (candidate.TryGetProperty("groundingMetadata", out var grounding) && grounding.ValueKind == JsonValueKind.Object
                && grounding.TryGetProperty("groundingChunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.ValueKind != JsonValueKind.Object || !chunk.TryGetProperty("web", out var web)
                        || web.ValueKind != JsonValueKind.Object) continue;
                    var uri = web.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(uri) || sources.Any(s => s.Url == uri)) continue;
                    var title = web.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    sources.Add(new ChatSource(string.IsNullOrWhiteSpace(title) ? uri : title, uri));
                }
            }

            var sb = new StringBuilder();
            if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        sb.Append(text.GetString());
                }
            }
            return sb.ToString();
        }
    }

    // ---- OpenAI ----

    private async Task<ChatReply> StreamOpenAiAsync(string model, string key, string body, Action<string> deliver, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(IdleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = false;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(watchdog.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var errorCode = OpenAiClient.ErrorCode(text);
            Log.Error($"Chat OpenAI {model}: HTTP {code} {errorCode}");
            // MapStatus already turns an empty account (insufficient_quota) into Billing, which is not retried.
            throw Mark(OpenAiClient.MapStatus(code, errorCode), code, false);
        }

        var reply = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(watchdog.Token).ConfigureAwait(false) is { } line)
        {
            watchdog.CancelAfter(IdleTimeout);
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line.Substring(5).Trim();
            if (json.Length == 0) continue;
            if (json == "[DONE]") break;
            var piece = ParseOpenAiChunk(json);
            if (piece.Length == 0) continue;
            reply.Append(piece);
            deliver(piece);
        }
        if (reply.Length == 0)
        {
            Log.Error($"Chat OpenAI {model}: 2xx without any text");
            throw new GeminiException("OpenAI", GeminiErrorKind.Server, "OpenAI hat keine Antwort geliefert. Bitte noch einmal versuchen.");
        }
        Log.Info($"Chat OpenAI {model}: {reply.Length} chars");
        return new ChatReply(reply.ToString(), Array.Empty<ChatSource>());
    }

    /// <summary>One OpenAI SSE object: choices[0].delta.content ("" when none). A top-level error object throws.</summary>
    internal static string ParseOpenAiChunk(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return ""; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var errorCode = OpenAiClient.ErrorCode(json);
                Log.Error($"Chat OpenAI: stream error object {errorCode}");
                throw Mark(OpenAiClient.MapStatus(500, errorCode), 500, false);
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";
            return "";
        }
    }

    // ---- helpers ----

    // Flags a mapped HTTP failure as retryable when the status can pass (429 unless the daily quota is gone, 5xx).
    private static GeminiException Mark(GeminiException ex, int code, bool dailyQuota)
    {
        if (HttpRetry.IsTransientStatus(code) && !(code == 429 && dailyQuota)
            && ex.Kind is GeminiErrorKind.Quota or GeminiErrorKind.Server)
            ex.Data[TransientKey] = true;
        return ex;
    }

    private static bool IsDaily(string body) =>
        body.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || body.Contains("per day", StringComparison.OrdinalIgnoreCase);
}
