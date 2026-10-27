using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
}

/// <summary>Gemini call failed. <see cref="Exception.Message"/> is a German text fit for the user.</summary>
public sealed class GeminiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GeminiException(GeminiErrorKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner) => Kind = kind;

    /// <summary>The error category.</summary>
    public GeminiErrorKind Kind { get; }
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
public sealed class GeminiClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _transcriptionModel;
    private readonly Func<string> _promptModel;

    private const int MaxAttempts = 5;
    private const int MaxServerErrorAttempts = 6;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetryInfoDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Base of the exponential backoff between retries (1.5 s, doubling per attempt, capped at 15 s;
    /// tests set it to zero). A 429 with a Google RetryInfo delay waits that delay plus 2 s instead.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Safety margin added to a Google RetryInfo delay (2 s; tests set it to zero).</summary>
    public TimeSpan RetryInfoBuffer { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Creates a client reading key and models from <paramref name="settings"/> on every call.</summary>
    public GeminiClient(Settings settings, HttpClient? http = null)
        : this(() => settings.ApiKey, () => settings.TranscriptionModel, () => settings.PromptModel, http) { }

    /// <summary>Creates a client with explicit providers (evaluated on every call).</summary>
    public GeminiClient(Func<string?> apiKeyProvider, Func<string> transcriptionModel,
        Func<string> promptModel, HttpClient? http = null)
    {
        _apiKey = apiKeyProvider;
        _transcriptionModel = transcriptionModel;
        _promptModel = promptModel;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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
        var body = BuildTranscriptionBody(model, wav, glossary);
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
    public async Task<string> PromptAsync(byte[] wav, string? selection, string? customSystemPrompt, CancellationToken ct = default)
    {
        var model = _promptModel();
        var body = BuildPromptBody(wav, selection, customSystemPrompt);
        return await SendAsync(model, body, ct).ConfigureAwait(false);
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
    public async Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct = default)
    {
        var model = _promptModel();
        var body = BuildPromptTextBody(selection, instruction, customSystemPrompt);
        return await SendAsync(model, body, ct).ConfigureAwait(false);
    }

    internal static string BuildTranscriptionBody(string model, byte[] wav, IReadOnlyList<string> glossary)
    {
        var instruction = Prompts.DefaultTranscriptionSystemPrompt;
        // Notes like `Bachata (not "Batchata")` become tie-breakers; the rejected spelling never goes out as vocabulary.
        var hint = GlossaryPrompt.Hint(glossary);
        if (hint.Length > 0) instruction += "\n\n" + hint;

        // 3.7 / 3.8 Flash and Pro reject "minimal" (HTTP 400): clamp to "low" like TranscriptionModels.swift.
        var level = RejectsMinimal(model) ? "low" : "minimal";
        var root = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = instruction },
                    AudioPart(wav)),
            }),
            ["generationConfig"] = new JsonObject
            {
                // Like TranscriptionModels.swift: no sampling noise in a transcript.
                ["temperature"] = 0.0,
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level },
            },
        };
        return root.ToJsonString();
    }

    internal static string BuildPromptBody(byte[] wav, string? selection, string? customSystemPrompt) =>
        BuildPromptRequest(customSystemPrompt,
            new JsonObject { ["text"] = DictatePromptText.SelectionBlock(selection) }, AudioPart(wav));

    internal static string BuildPromptTextBody(string? selection, string instruction, string? customSystemPrompt) =>
        BuildPromptRequest(customSystemPrompt,
            new JsonObject { ["text"] = DictatePromptText.LocalUserTurn(selection, instruction) });

    // Shared by the audio and the text-only prompt call so both keep the same system prompt and thinking level.
    private static string BuildPromptRequest(string? customSystemPrompt, params JsonObject[] userParts)
    {
        var system = DictatePromptText.SystemPrompt(customSystemPrompt);
        var root = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(userParts.Select(p => (JsonNode)p).ToArray()),
            }),
            // Deliberate Windows choice: the Mac sends no generationConfig here, but 3.7/3.8 reject "minimal".
            ["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" },
            },
        };
        return root.ToJsonString();
    }

    private static bool RejectsMinimal(string model) =>
        model.Contains("gemini-3.7-flash", StringComparison.OrdinalIgnoreCase)
        || model.Contains("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase)
        || model.Contains("-pro", StringComparison.OrdinalIgnoreCase);

    private static JsonObject AudioPart(byte[] wav) => new()
    {
        ["inline_data"] = new JsonObject
        {
            ["mime_type"] = "audio/wav",
            ["data"] = Convert.ToBase64String(wav),
        },
    };

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

    private async Task<string> SendAsync(string model, string json, CancellationToken ct)
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
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("x-goog-api-key", key);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Log.Info($"Gemini {model}: HTTP {(int)response.StatusCode}, request {json.Length} bytes, answer {text.Length} bytes, attempt {attempt}");
                    return ExtractText(text);
                }

                var code = (int)response.StatusCode;
                Log.Error($"Gemini {model}: HTTP {code} {GoogleError(text)}");
                failure = MapStatus(response.StatusCode, text);
                kind = $"HTTP {code}";
                // A daily quota will not recover within seconds: fail at once.
                if (code is not (429 or 500 or 502 or 503 or 504) || (code == 429 && IsDailyQuota(text)))
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
            if (attempt >= (serverError ? MaxServerErrorAttempts : MaxAttempts)) throw failure;
            TimeSpan delay;
            if (retryInfo is { } info)
            {
                // Google says when the limit lifts; a longer wait is not worth blocking dictation for.
                if (info > MaxRetryInfoDelay) throw failure;
                delay = info + RetryInfoBuffer;
            }
            else
            {
                delay = TimeSpan.FromTicks((long)Math.Min(
                    RetryDelay.Ticks * Math.Pow(2, attempt - 1), MaxBackoff.Ticks));
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
        return new GeminiException(GeminiErrorKind.Server, $"Gemini-Fehler (HTTP {code}). Bitte später erneut versuchen.");
    }

    /// <summary>Google's error.status and error.message for the log (no request content in there).</summary>
    internal static string GoogleError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var e = doc.RootElement.GetProperty("error");
            string status = e.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            string msg = e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return $"{status}: {(msg.Length > 300 ? msg[..300] : msg)}";
        }
        catch { return ""; }
    }

    /// <summary>Concatenates candidates[0].content.parts[].text; "" when absent.</summary>
    internal static string ExtractText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands)
                || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0) return "";
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
