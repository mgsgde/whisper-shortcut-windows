using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core;

/// <summary>
/// OpenAI client for dictation (audio/transcriptions) and Dictate Prompt (chat/completions). Key and model
/// ids are read at call time. Results are raw model text: run <see cref="TextCleanup"/> on them. Errors reuse
/// <see cref="GeminiException"/>. Transient failures (429, 500, 502, 503, 504, network errors, per-attempt
/// timeouts) are retried like in <see cref="GeminiClient"/>: up to 5 attempts (6 for 5xx), exponential backoff.
/// </summary>
public sealed class OpenAiClient : ISpeechProvider, ITextProvider
{
    private const string BaseUrl = "https://api.openai.com/v1/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 5;
    private const int MaxServerErrorAttempts = 6;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>Output-token ceiling for edit calls, so a long selection gets room and a cut-off reply is detectable.</summary>
    internal const int MaxCompletionTokens = 32000;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _transcriptionModel;
    private readonly Func<string> _promptModel;

    /// <summary>Base of the exponential backoff between retries (1.5 s, doubling, capped at 15 s; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Creates a client reading key and models from <paramref name="settings"/> on every call.</summary>
    public OpenAiClient(Settings settings, HttpClient? http = null)
        : this(() => settings.OpenAiKey, () => settings.OpenAiTranscriptionModel, () => settings.OpenAiPromptModel, http) { }

    /// <summary>Creates a client with explicit providers (evaluated on every call).</summary>
    public OpenAiClient(Func<string?> apiKeyProvider, Func<string> transcriptionModel,
        Func<string> promptModel, HttpClient? http = null)
    {
        _apiKey = apiKeyProvider;
        _transcriptionModel = transcriptionModel;
        _promptModel = promptModel;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Transcribes a WAV (temperature 0). Returns the raw text, "" if nothing was recognised.</summary>
    /// <param name="wav">WAV bytes.</param>
    /// <param name="glossary">Spelling reference terms; empty list sends no prompt.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default)
    {
        var model = _transcriptionModel();
        var hint = GlossaryPrompt.Hint(glossary);
        HttpContent Build()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("0"), "temperature");
            form.Add(new StringContent("json"), "response_format");
            if (hint.Length > 0) form.Add(new StringContent(hint, Encoding.UTF8), "prompt");
            return form;
        }
        var answer = await SendAsync("audio/transcriptions", model, Build, wav.Length, ct).ConfigureAwait(false);
        return ExtractTranscript(answer);
    }

    /// <summary>
    /// Dictate Prompt: applies the typed <paramref name="instruction"/> to <paramref name="selection"/>
    /// (or composes new text when null/empty). Returns the raw model text.
    /// </summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct = default)
    {
        var model = _promptModel();
        var json = BuildChatBody(model, selection, instruction, customSystemPrompt);
        var answer = await SendAsync("chat/completions", model,
            () => new StringContent(json, Encoding.UTF8, "application/json"), json.Length, ct).ConfigureAwait(false);
        return ExtractChatText(answer);
    }

    internal static string BuildChatBody(string model, string? selection, string instruction, string? customSystemPrompt) =>
        new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = DictatePromptText.SystemPrompt(customSystemPrompt) },
                new JsonObject { ["role"] = "user", ["content"] = DictatePromptText.LocalUserTurn(selection, instruction) }),
            // WHY: a rewritten long selection is as long as the selection; a cut-off reply must be detectable.
            ["max_completion_tokens"] = MaxCompletionTokens,
            ["reasoning_effort"] = "low",
        }.ToJsonString();

    /// <summary>
    /// Checks an API key with a models-list request before it is saved. Never throws for network
    /// problems; caller cancellation propagates as <see cref="OperationCanceledException"/>.
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
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            result = (int)response.StatusCode switch
            {
                200 => KeyCheck.Ok,
                401 => KeyCheck.Invalid,
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
        Log.Info($"OpenAI key check: {result}");
        return result;
    }

    // The body is rebuilt per attempt: a multipart content cannot be sent twice.
    private async Task<string> SendAsync(string path, string model, Func<HttpContent> buildContent, int size, CancellationToken ct)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new GeminiException("OpenAI", GeminiErrorKind.NoKey,
                "Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.");

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Upload time scales with the body (~40 kB/s floor, e.g. slow DSL).
            timeout.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(size / 40_000.0));
            GeminiException failure;
            string kind;
            var serverError = false;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = buildContent() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    Log.Info($"OpenAI {model}: HTTP {(int)response.StatusCode}, request {size} bytes, answer {text.Length} bytes, attempt {attempt}");
                    return text;
                }

                var code = (int)response.StatusCode;
                var errorCode = ErrorCode(text);
                Log.Error($"OpenAI {model}: HTTP {code} {errorCode}");
                failure = MapStatus(code, errorCode);
                kind = $"HTTP {code}";
                // An empty account will not recover within seconds: fail at once.
                if (code is not (429 or 500 or 502 or 503 or 504) || (code == 429 && errorCode == "insufficient_quota"))
                    throw failure;
                serverError = code >= 500;
                if (code == 429) retryAfter = ParseRetryAfter(response.Headers);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Error($"OpenAI {model}: timeout");
                failure = new GeminiException("OpenAI", GeminiErrorKind.Timeout,
                    "Zeitüberschreitung: OpenAI hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.");
                kind = "timeout";
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"OpenAI {model}: network", ex);
                failure = new GeminiException("OpenAI", GeminiErrorKind.Network,
                    "Kein Netz: OpenAI ist nicht erreichbar. Bitte Internetverbindung prüfen.", ex);
                kind = "network";
            }

            if (attempt >= (serverError ? MaxServerErrorAttempts : MaxAttempts)) throw failure;
            TimeSpan delay;
            if (retryAfter is { } wait)
            {
                // OpenAI says when the limit lifts; a longer wait is not worth blocking dictation for.
                if (wait > MaxRetryAfter) throw failure;
                delay = wait;
            }
            else
            {
                delay = TimeSpan.FromTicks((long)Math.Min(
                    RetryDelay.Ticks * Math.Pow(2, attempt - 1), MaxBackoff.Ticks));
            }
            Log.Info($"OpenAI {model}: attempt {attempt} failed ({kind}), retrying in {delay.TotalSeconds:0.#} s");
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static GeminiException MapStatus(int code, string errorCode)
    {
        if (code == 401)
            return new GeminiException("OpenAI", GeminiErrorKind.InvalidKey, "OpenAI-Schlüssel ungültig. Bitte in den Einstellungen prüfen.");
        if (code == 403)
            return new GeminiException("OpenAI", GeminiErrorKind.InvalidKey,
                "Dieser OpenAI-Schlüssel darf das Modell nicht benutzen (Projekt- oder Organisationsrechte prüfen).");
        if (code == 429)
            return new GeminiException("OpenAI", GeminiErrorKind.Quota, errorCode == "insufficient_quota"
                ? "OpenAI-Guthaben aufgebraucht. Bitte das Konto unter platform.openai.com prüfen."
                : "OpenAI ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.");
        return new GeminiException("OpenAI", GeminiErrorKind.Server, $"OpenAI-Fehler (HTTP {code}). Bitte später erneut versuchen.");
    }

    /// <summary>error.code, else error.type, of an OpenAI error body ("insufficient_quota" wins if either has it); "" when absent. Never the message (it can quote content).</summary>
    private static string ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object) return "";
            string? Get(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var code = Get("code");
            var type = Get("type");
            if (code == "insufficient_quota" || type == "insufficient_quota") return "insufficient_quota";
            return code ?? type ?? "";
        }
        catch (JsonException) { return ""; }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var inv = CultureInfo.InvariantCulture;
        if (headers.TryGetValues("retry-after-ms", out var ms)
            && double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, inv, out var m) && m >= 0 && m < 86_400_000)
            return TimeSpan.FromMilliseconds(m);
        if (headers.TryGetValues("retry-after", out var s)
            && double.TryParse(s.FirstOrDefault(), NumberStyles.Float, inv, out var secs) && secs >= 0 && secs < 86_400)
            return TimeSpan.FromSeconds(secs);
        return null;
    }

    internal static string ExtractTranscript(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>choices[0].message.content; "" when absent. A "length" finish throws, so a cut-off rewrite is never pasted over the selection.</summary>
    internal static string ExtractChatText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return "";
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind == JsonValueKind.String && fr.GetString() == "length")
            {
                Log.Error("OpenAI: finish_reason length, reply is cut off");
                throw new GeminiException("OpenAI", GeminiErrorKind.Truncated,
                    "Die Antwort ist zu lang geworden und wurde abgeschnitten. Bitte einen kleineren Teil markieren.");
            }
            return choice.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }
}
