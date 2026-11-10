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
    /// <summary>Extra tries after the first one.</summary>
    public const int MaxRetries = 2;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _geminiKey;
    private readonly Func<string?> _openAiKey;
    private readonly HttpClient _http;

    /// <summary>Base of the backoff between retries (1.5 s, doubling; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Safety margin added to a Google RetryInfo delay (2 s; tests set it to zero).</summary>
    public TimeSpan RetryInfoBuffer { get; set; } = TimeSpan.FromSeconds(2);

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
        IReadOnlyList<ChatMessage> messages, Action<string>? onDelta, bool webSearch, CancellationToken ct, string? thinkingLevel = null)
    {
        if (engine == Engine.Local)
            throw new ArgumentException("Der Offline-Motor wird von der App selbst bedient.", nameof(engine));
        var gemini = engine == Engine.Gemini;
        var api = gemini ? CloudApi.Gemini : CloudApi.OpenAi;
        var key = (gemini ? _geminiKey() : _openAiKey())?.Trim();
        if (string.IsNullOrEmpty(key)) throw api.NoKey(KeyHint.Settings);

        var delivered = false;
        void Deliver(string piece)
        {
            if (piece.Length == 0) return;
            delivered = true;
            onDelta?.Invoke(piece);
        }

        var body = gemini
            ? BuildGeminiBody(model, systemPrompt, messages, webSearch, thinkingLevel)
            : BuildOpenAiBody(model, systemPrompt, messages, thinkingLevel);

        // Once a piece of text reached the caller a retry would repeat it: the error ends the answer.
        var policy = new RetryPolicy(MaxRetries + 1, MaxRetries + 1, RetryDelay, gemini ? RetryInfoBuffer : TimeSpan.Zero);
        return await HttpRetry.RunAsync(_ => gemini
                ? StreamGeminiAsync(model, key, body, Deliver, ct)
                : StreamOpenAiAsync(model, key, body, Deliver, ct),
            policy, $"Chat {api.Name} {model}", ct, mayRetry: () => !delivered).ConfigureAwait(false);
    }

    // ---- request bodies ----

    internal static string BuildGeminiBody(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages, bool webSearch,
        string? thinkingLevel = null)
    {
        var contents = new JsonArray();
        foreach (var m in messages)
        {
            var hasFiles = m.Attachments is { Count: > 0 };
            if (string.IsNullOrWhiteSpace(m.Text) && !hasFiles) continue; // Gemini rejects empty parts
            var parts = new JsonArray();
            // Every turn's attachments go out again on every request (like the Mac).
            if (hasFiles)
                foreach (var a in m.Attachments!)
                    parts.Add(new JsonObject
                    {
                        ["inline_data"] = new JsonObject { ["mime_type"] = a.MimeType, ["data"] = a.DataBase64 },
                    });
            if (!string.IsNullOrWhiteSpace(m.Text)) parts.Add(new JsonObject { ["text"] = m.Text });
            contents.Add(new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "model", ["parts"] = parts });
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }),
            },
            ["contents"] = contents,
        };
        // "low", not "minimal", where the model rejects "minimal" (see ChatThinking).
        if (ChatThinking.GeminiConfig(model, thinkingLevel) is { } thinking)
            body["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinking };
        if (webSearch)
            body["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });
        return body.ToJsonString(Compact);
    }

    internal static string BuildOpenAiBody(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages,
        string? thinkingLevel = null)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            list.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var m in messages)
        {
            var hasFiles = m.Role == ChatRole.User && m.Attachments is { Count: > 0 };
            if (string.IsNullOrWhiteSpace(m.Text) && !hasFiles) continue;
            JsonNode content = m.Text;
            if (hasFiles)
            {
                var parts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(m.Text)) parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var a in m.Attachments!) parts.Add(OpenAiAttachmentPart(a));
                content = parts;
            }
            list.Add(new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "assistant", ["content"] = content });
        }
        var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = list };
        if (ChatThinking.OpenAiEffort(model, thinkingLevel) is { } effort) body["reasoning_effort"] = effort;
        return body.ToJsonString(Compact);
    }

    private static JsonObject OpenAiAttachmentPart(ChatAttachment a)
    {
        if (ChatAttachments.IsImage(a))
            return new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{a.MimeType};base64,{a.DataBase64}" },
            };
        if (a.MimeType == "application/pdf")
            return new JsonObject
            {
                ["type"] = "file",
                ["file"] = new JsonObject
                {
                    ["filename"] = a.FileName,
                    ["file_data"] = $"data:application/pdf;base64,{a.DataBase64}",
                },
            };
        return new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"Datei „{a.FileName}“:\n{ChatAttachments.DecodeText(a)}",
        };
    }

    // ---- Gemini ----

    private async Task<ChatReply> StreamGeminiAsync(string model, string key, string body, Action<string> deliver, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(IdleTimeout);
        return await CloudHttp.SendAsync(_http, CloudApi.Gemini, $"{CloudApi.Gemini.ApiBase}{model}:streamGenerateContent?alt=sse", key,
            new StringContent(body, Encoding.UTF8, "application/json"), $"Chat Gemini {model}", watchdog.Token, ct,
            async response =>
            {
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
            }, HttpCompletionOption.ResponseHeadersRead, "stalled").ConfigureAwait(false);
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

            if (CloudApi.Gemini.StreamError(root, json, "Chat Gemini", "die Anfrage") is { } streamError) throw streamError;

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
        return await CloudHttp.SendAsync(_http, CloudApi.OpenAi, CloudApi.OpenAi.ApiBase + "chat/completions", key,
            new StringContent(body, Encoding.UTF8, "application/json"), $"Chat OpenAI {model}", watchdog.Token, ct,
            async response =>
            {
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
            }, HttpCompletionOption.ResponseHeadersRead, "stalled").ConfigureAwait(false);
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
            if (CloudApi.OpenAi.StreamError(root, json, "Chat OpenAI", "") is { } streamError) throw streamError;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";
            return "";
        }
    }
}
