using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>What went wrong, for UI decisions.</summary>
public enum GeminiErrorKind
{
    /// <summary>No key entered.</summary>
    NoKey,
    /// <summary>Key rejected by Gemini.</summary>
    InvalidKey,
    /// <summary>No network / host unreachable.</summary>
    Network,
    /// <summary>Quota or rate limit (HTTP 429).</summary>
    Quota,
    /// <summary>No answer within 60 s.</summary>
    Timeout,
    /// <summary>Any other HTTP/server error.</summary>
    Server,
    /// <summary>The reply hit the output-token limit and is cut off (finishReason MAX_TOKENS).</summary>
    Truncated,
}

/// <summary>Gemini (or OpenAI) call failed. <see cref="Exception.Message"/> is a German text fit for the user.</summary>
public sealed class GeminiException : Exception
{
    /// <summary>Creates the exception for a Gemini call.</summary>
    public GeminiException(GeminiErrorKind kind, string userMessage, Exception? inner = null)
        : this("Gemini", kind, userMessage, inner) { }

    /// <summary>Creates the exception for the named provider ("OpenAI").</summary>
    public GeminiException(string provider, GeminiErrorKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner) => (Provider, Kind) = (provider, kind);

    /// <summary>The error category.</summary>
    public GeminiErrorKind Kind { get; }

    /// <summary>"Gemini" or "OpenAI", for the notification title.</summary>
    public string Provider { get; }
}

/// <summary>Result of <see cref="GeminiClient.CheckKeyAsync"/>.</summary>
public enum KeyCheck
{
    /// <summary>Google accepted the key.</summary>
    Ok,
    /// <summary>Blank key, or Google rejected it (400/401).</summary>
    Invalid,
    /// <summary>403: a real key that may not use the Gemini API (API off, restricted, or blocked).</summary>
    Forbidden,
    /// <summary>Google could not be reached (network error or timeout).</summary>
    NoConnection,
    /// <summary>Any other answer (e.g. 429, 5xx); the key may still be fine.</summary>
    Other,
}

/// <summary>
/// Gemini generateContent client for dictation and Dictate Prompt. The API key and model ids
/// are read at call time, so values entered later in the settings window work without restart.
/// Results are raw model text: run <see cref="TextCleanup"/> on them. Transient failures (429, 500,
/// 502, 503, 504, network errors, per-attempt timeouts) are retried like on the Mac: up to 5 attempts
/// (6 for 5xx) with exponential backoff, or Google's own RetryInfo delay on a 429.
/// </summary>
public sealed class GeminiClient : ISpeechProvider, ITextProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _transcriptionModel;
    private readonly Func<string> _promptModel;
    private readonly Func<string>? _transcriptionPrompt;

    /// <summary>
    /// Output-token ceiling of the current Gemini flash models. Set explicitly on edit calls so a long
    /// selection gets the whole room the model has instead of whatever the server default allows.
    /// </summary>
    internal const int MaxOutputTokens = 65536;

    private static readonly TimeSpan MaxRetryInfoDelay = HttpRetry.MaxWait;

    /// <summary>
    /// Base of the exponential backoff between retries (1.5 s, doubling per attempt, capped at 15 s;
    /// tests set it to zero). A 429 with a Google RetryInfo delay waits that delay plus 2 s instead.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Safety margin added to a Google RetryInfo delay (2 s; tests set it to zero).</summary>
    public TimeSpan RetryInfoBuffer { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Creates a client reading key and models from <paramref name="settings"/> on every call.</summary>
    public GeminiClient(Settings settings, HttpClient? http = null)
        : this(() => settings.ApiKey, () => settings.TranscriptionModel, () => settings.PromptModel, http, () => settings.DictateSystemPrompt) { }

    /// <summary>Creates a client with explicit providers (evaluated on every call).</summary>
    public GeminiClient(Func<string?> apiKeyProvider, Func<string> transcriptionModel,
        Func<string> promptModel, HttpClient? http = null, Func<string>? transcriptionPrompt = null)
    {
        _transcriptionPrompt = transcriptionPrompt;
        _apiKey = apiKeyProvider;
        _transcriptionModel = transcriptionModel;
        _promptModel = promptModel;
        _http = http ?? NetworkPolicy.CreateApiClient();
    }

    /// <summary>
    /// Transcribes a WAV (temperature 0 for a deterministic transcript). Returns the raw model text,
    /// "" if nothing was recognised. Transient failures are retried, see <see cref="GeminiClient"/>.
    /// </summary>
    /// <param name="wav">WAV bytes.</param>
    /// <param name="glossary">Spelling reference terms; empty list adds no hint.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default)
    {
        var model = _transcriptionModel();
        var prompt = _transcriptionPrompt?.Invoke();
        // Base64+JSON of a dictation is megabytes: keep it off the UI thread that called us.
        var body = await Task.Run(() => BuildTranscriptionBodyUtf8(model, wav, glossary, prompt), ct).ConfigureAwait(false);
        return await SendAsync(model, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dictate Prompt: applies the spoken instruction to <paramref name="selection"/> (or composes
    /// new text when null/empty). Returns the raw model text, "" if nothing was recognised.
    /// </summary>
    /// <param name="wav">WAV bytes of the spoken instruction.</param>
    /// <param name="selection">Selected text, or null/empty for a compose turn.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> PromptAsync(byte[] wav, string? selection, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null)
    {
        var model = _promptModel();
        var body = await Task.Run(() => BuildPromptBodyUtf8(wav, selection, customSystemPrompt, history), ct).ConfigureAwait(false);
        return await SendAsync(model, body, ct, rejectTruncated: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Dictate Prompt without audio: applies the typed <paramref name="instruction"/> to
    /// <paramref name="selection"/> (or composes new text when null/empty). Same model, thinking level
    /// and system prompt as <see cref="PromptAsync"/>. Returns the raw model text.
    /// </summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text (a quick action, optionally with spoken additions).</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null)
    {
        var model = _promptModel();
        var body = BuildPromptTextBodyUtf8(selection, instruction, customSystemPrompt, history);
        return await SendAsync(model, body, ct, rejectTruncated: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the TLS session to Gemini while the user is still speaking, so Stop does not pay the
    /// handshake on the hot path. Failures are ignored: the real request will try again.
    /// </summary>
    public Task PrewarmConnection() =>
        HttpPrewarm.Connect(_http, "https://generativelanguage.googleapis.com/", "Gemini");

    internal static string BuildTranscriptionBody(string model, byte[] wav, IReadOnlyList<string> glossary, string? customPrompt = null) =>
        Encoding.UTF8.GetString(BuildTranscriptionBodyUtf8(model, wav, glossary, customPrompt));

    internal static string BuildPromptBody(byte[] wav, string? selection, string? customSystemPrompt) =>
        Encoding.UTF8.GetString(BuildPromptBodyUtf8(wav, selection, customSystemPrompt));

    internal static string BuildPromptTextBody(string? selection, string instruction, string? customSystemPrompt) =>
        Encoding.UTF8.GetString(BuildPromptTextBodyUtf8(selection, instruction, customSystemPrompt));

    /// <summary>
    /// Writes the generateContent body with <see cref="Utf8JsonWriter.WriteBase64String(string, ReadOnlySpan{byte})"/>
    /// so a 2-minute chunk is not first turned into a base64 string, then a JsonObject, then a JSON string.
    /// </summary>
    internal static byte[] BuildTranscriptionBodyUtf8(string model, byte[] wav, IReadOnlyList<string> glossary, string? customPrompt = null)
    {
        var instruction = string.IsNullOrWhiteSpace(customPrompt) ? Prompts.DefaultTranscriptionSystemPrompt : customPrompt.Trim();
        // Notes like `Bachata (not "Batchata")` become tie-breakers; the rejected spelling never goes out as vocabulary.
        var hint = GlossaryPrompt.Hint(glossary);
        if (hint.Length > 0) instruction += "\n\n" + hint;

        // 3.7 / 3.8 Flash and Pro reject "minimal" (HTTP 400): clamp to "low" like TranscriptionModels.swift.
        var level = RejectsMinimal(model) ? "low" : "minimal";
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(wav.Length, instruction.Length));
        using (var w = new Utf8JsonWriter(writer))
        {
            w.WriteStartObject();
            w.WritePropertyName("contents");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WritePropertyName("parts");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("text", instruction);
            w.WriteEndObject();
            WriteAudioPart(w, wav);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WritePropertyName("generationConfig");
            w.WriteStartObject();
            // Like TranscriptionModels.swift: no sampling noise in a transcript.
            w.WriteNumber("temperature", 0.0);
            w.WritePropertyName("thinkingConfig");
            w.WriteStartObject();
            w.WriteString("thinkingLevel", level);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return writer.WrittenSpan.ToArray();
    }

    internal static byte[] BuildPromptBodyUtf8(byte[] wav, string? selection, string? customSystemPrompt,
        IReadOnlyList<PromptTurn>? history = null)
    {
        var system = DictatePromptText.SystemPrompt(customSystemPrompt);
        var selectionText = DictatePromptText.SelectionBlock(selection);
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(wav.Length, system.Length + selectionText.Length + HistoryChars(history)));
        using (var w = new Utf8JsonWriter(writer))
        {
            WritePromptEnvelope(w, system, history, () =>
            {
                w.WriteStartObject();
                w.WriteString("text", selectionText);
                w.WriteEndObject();
                WriteAudioPart(w, wav);
            });
        }
        return writer.WrittenSpan.ToArray();
    }

    internal static byte[] BuildPromptTextBodyUtf8(string? selection, string instruction, string? customSystemPrompt,
        IReadOnlyList<PromptTurn>? history = null)
    {
        var system = DictatePromptText.SystemPrompt(customSystemPrompt);
        var user = DictatePromptText.LocalUserTurn(selection, instruction);
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(0, system.Length + user.Length + HistoryChars(history)));
        using (var w = new Utf8JsonWriter(writer))
        {
            WritePromptEnvelope(w, system, history, () =>
            {
                w.WriteStartObject();
                w.WriteString("text", user);
                w.WriteEndObject();
            });
        }
        return writer.WrittenSpan.ToArray();
    }

    // Shared by the audio and the text-only prompt call so both keep the same system prompt and thinking level.
    // Earlier turns (PromptConversationMemory) go before the new one as user/model pairs.
    private static void WritePromptEnvelope(Utf8JsonWriter w, string system, IReadOnlyList<PromptTurn>? history, Action writeUserParts)
    {
        w.WriteStartObject();
        w.WritePropertyName("systemInstruction");
        w.WriteStartObject();
        w.WritePropertyName("parts");
        w.WriteStartArray();
        w.WriteStartObject();
        w.WriteString("text", system);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WritePropertyName("contents");
        w.WriteStartArray();
        foreach (var turn in history ?? Array.Empty<PromptTurn>())
        {
            WriteTextTurn(w, "user", DictatePromptText.LocalUserTurn(turn.SelectedText, turn.UserInstruction));
            WriteTextTurn(w, "model", turn.ModelResponse);
        }
        w.WriteStartObject();
        w.WriteString("role", "user");
        w.WritePropertyName("parts");
        w.WriteStartArray();
        writeUserParts();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();
        // Deliberate Windows choice: the Mac sends no generationConfig here, but 3.7/3.8 reject "minimal".
        w.WritePropertyName("generationConfig");
        w.WriteStartObject();
        w.WritePropertyName("thinkingConfig");
        w.WriteStartObject();
        w.WriteString("thinkingLevel", "low");
        w.WriteEndObject();
        // WHY: a rewritten long selection is as long as the selection; a cut-off reply must be detectable.
        w.WriteNumber("maxOutputTokens", MaxOutputTokens);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteTextTurn(Utf8JsonWriter w, string role, string text)
    {
        w.WriteStartObject();
        w.WriteString("role", role);
        w.WritePropertyName("parts");
        w.WriteStartArray();
        w.WriteStartObject();
        w.WriteString("text", text);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static int HistoryChars(IReadOnlyList<PromptTurn>? history) =>
        history?.Sum(t => (t.SelectedText?.Length ?? 0) + t.UserInstruction.Length + t.ModelResponse.Length + 64) ?? 0;

    private static void WriteAudioPart(Utf8JsonWriter w, byte[] wav)
    {
        w.WriteStartObject();
        w.WritePropertyName("inline_data");
        w.WriteStartObject();
        w.WriteString("mime_type", "audio/wav");
        w.WriteBase64String("data", wav);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // JSON + base64 is ~4/3 of the WAV plus the instruction; size the writer so it does not grow twice.
    private static int JsonCapacity(int wavBytes, int textChars) =>
        Math.Max(256, 64 + textChars * 3 + wavBytes + wavBytes / 3);

    private static bool RejectsMinimal(string model) =>
        model.Contains("gemini-3.7-flash", StringComparison.OrdinalIgnoreCase)
        || model.Contains("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase)
        || model.Contains("-pro", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks an API key with a cheap models-list request before it is saved. Never throws for
    /// network problems; caller cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<KeyCheck> CheckKeyAsync(string key, CancellationToken ct = default)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return KeyCheck.Invalid;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        KeyCheck result;
        try
        {
            // Key goes in the header, not the URL, so it cannot end up in proxy or server logs.
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "?pageSize=1");
            request.Headers.Add("x-goog-api-key", key);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            result = (int)response.StatusCode switch
            {
                200 => KeyCheck.Ok,
                400 or 401 => KeyCheck.Invalid,
                403 => KeyCheck.Forbidden,
                _ => KeyCheck.Other,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = KeyCheck.NoConnection;
        }
        catch (HttpRequestException)
        {
            result = KeyCheck.NoConnection;
        }
        Log.Info($"Gemini key check: {result}");
        return result;
    }

    private static ByteArrayContent JsonUtf8Content(byte[] json)
    {
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<string> SendAsync(string model, byte[] json, CancellationToken ct, bool rejectTruncated = false)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new GeminiException(GeminiErrorKind.NoKey,
                "Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.");

        var url = $"{BaseUrl}{model}:generateContent";
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Upload time scales with the body (~40 kB/s floor, e.g. slow DSL).
            timeout.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(json.Length / 40_000.0));
            GeminiException failure;
            string kind;
            var serverError = false;
            TimeSpan? retryInfo = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonUtf8Content(json),
                };
                request.Headers.ExpectContinue = false;
                request.Headers.Add("x-goog-api-key", key);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Log.Info($"Gemini {model}: HTTP {(int)response.StatusCode}, request {json.Length} bytes, answer {text.Length} bytes, attempt {attempt}");
                    return ExtractText(text, rejectTruncated);
                }

                var code = (int)response.StatusCode;
                Log.Error($"Gemini {model}: HTTP {code} {GoogleStatus(text)}");
                failure = MapStatus(response.StatusCode, text);
                kind = $"HTTP {code}";
                // A daily quota will not recover within seconds: fail at once.
                if (!HttpRetry.IsTransientStatus(code) || (code == 429 && IsDailyQuota(text)))
                    throw failure;
                serverError = code >= 500;
                if (code == 429) retryInfo = ParseRetryInfo(text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Error($"Gemini {model}: timeout");
                failure = new GeminiException(GeminiErrorKind.Timeout,
                    "Zeitüberschreitung: Gemini hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.");
                kind = "timeout";
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Gemini {model}: network", ex);
                failure = new GeminiException(GeminiErrorKind.Network,
                    "Kein Netz: Gemini ist nicht erreichbar. Bitte Internetverbindung prüfen.", ex);
                kind = "network";
            }

            // Retryable failure: 5 attempts, 6 when the server itself is failing.
            if (attempt >= (serverError ? HttpRetry.MaxServerErrorAttempts : HttpRetry.MaxAttempts)) throw failure;
            TimeSpan delay;
            if (retryInfo is { } info)
            {
                // Google says when the limit lifts; a longer wait is not worth blocking dictation for.
                if (info > MaxRetryInfoDelay) throw failure;
                delay = info + RetryInfoBuffer;
            }
            else
            {
                delay = HttpRetry.Backoff(RetryDelay, attempt);
            }
            Log.Info($"Gemini {model}: attempt {attempt} failed ({kind}), retrying in {delay.TotalSeconds:0.#} s");
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True when a 429 body says the per-day quota is used up (retrying within the minute is pointless).</summary>
    private static bool IsDailyQuota(string body) =>
        body.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
        || body.Contains("per day", StringComparison.OrdinalIgnoreCase);

    /// <summary>The delay from a Google RetryInfo detail ("55s", "1.5s"), or null when absent.</summary>
    public static TimeSpan? ParseRetryInfo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e)
                || !e.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array) return null;
            foreach (var d in details.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object
                    || !d.TryGetProperty("@type", out var t) || t.ValueKind != JsonValueKind.String
                    || !(t.GetString() ?? "").EndsWith("RetryInfo", StringComparison.Ordinal)
                    || !d.TryGetProperty("retryDelay", out var r) || r.ValueKind != JsonValueKind.String) continue;
                var str = (r.GetString() ?? "").Trim().TrimEnd('s');
                if (double.TryParse(str, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs >= 0 && secs < 86_400)
                    return TimeSpan.FromSeconds(secs);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static GeminiException MapStatus(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (body.Contains("SERVICE_DISABLED") || body.Contains("has not been used in project"))
            return new GeminiException(GeminiErrorKind.InvalidKey,
                "Die Gemini-API ist im Google-Projekt dieses Schlüssels nicht aktiviert (Generative Language API).");
        if (body.Contains("BILLING", StringComparison.Ordinal))
            return new GeminiException(GeminiErrorKind.InvalidKey,
                "Für das Google-Projekt dieses Schlüssels ist keine Abrechnung eingerichtet.");
        if (code is 401 or 403 || (code == 400 &&
            (body.Contains("API_KEY_INVALID") || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))))
            return new GeminiException(GeminiErrorKind.InvalidKey,
                "API-Schlüssel ungültig. Bitte in den Einstellungen prüfen.");
        if (code == 429)
            // Daily quota vs. a per-minute rate limit: only the second one passes by waiting a moment.
            return new GeminiException(GeminiErrorKind.Quota, IsDailyQuota(body)
                ? "Kontingent erschöpft. Bitte später erneut versuchen oder das Google-Konto prüfen."
                : "Gemini ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.");
        if (code == 404)
            return new GeminiException(GeminiErrorKind.Server, UserCopy.ModelGone("Gemini"));
        return new GeminiException(GeminiErrorKind.Server, UserCopy.ServerBusy("Gemini"));
    }

    /// <summary>Only the status word (e.g. RESOURCE_EXHAUSTED) of a Google error body, for the log: the message can quote request content.</summary>
    internal static string GoogleStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("status", out var s)
                && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>
    /// Concatenates candidates[0].content.parts[].text; "" when absent. With <paramref name="rejectTruncated"/>
    /// a MAX_TOKENS finish throws, so a cut-off rewrite is never pasted over the selection.
    /// </summary>
    internal static string ExtractText(string responseJson, bool rejectTruncated = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands)
                || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0) return "";
            if (rejectTruncated && cands[0].TryGetProperty("finishReason", out var fr)
                && fr.ValueKind == JsonValueKind.String && fr.GetString() == "MAX_TOKENS")
            {
                Log.Error("Gemini: finishReason MAX_TOKENS, reply is cut off");
                throw new GeminiException(GeminiErrorKind.Truncated,
                    "Die Antwort ist zu lang geworden und wurde abgeschnitten. Bitte einen kleineren Teil markieren.");
            }
            if (!cands[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array) return "";
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
