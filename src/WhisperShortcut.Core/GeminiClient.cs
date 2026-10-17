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

/// <summary>
/// Gemini generateContent client for dictation and Dictate Prompt. The API key and model ids
/// are read at call time, so values entered later in the settings window work without restart.
/// Results are raw model text: run <see cref="TextCleanup"/> on them.
/// </summary>
public sealed class GeminiClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _transcriptionModel;
    private readonly Func<string> _promptModel;

    /// <summary>Delay before the single retry on 429/500/503 (1 s; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

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
    /// Transcribes a WAV. Returns the raw model text, "" if nothing was recognised.
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

    internal static string BuildTranscriptionBody(string model, byte[] wav, IReadOnlyList<string> glossary)
    {
        var terms = glossary.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var instruction = Prompts.DefaultTranscriptionSystemPrompt;
        if (terms.Count > 0) instruction += "\n\n" + Prompts.GlossaryHintHeader + string.Join(", ", terms);

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
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level },
            },
        };
        return root.ToJsonString();
    }

    internal static string BuildPromptBody(byte[] wav, string? selection, string? customSystemPrompt)
    {
        var trimmed = customSystemPrompt?.Trim();
        var system = (string.IsNullOrEmpty(trimmed) ? Prompts.DefaultPromptModeSystemPrompt : trimmed)
            + Prompts.PromptModeOutputRule;
        var first = string.IsNullOrEmpty(selection)
            ? Prompts.DictatePromptComposeMarker
            : $"{Prompts.ClipboardSelectionHeader}\n\n{selection}";
        var root = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = first }, AudioPart(wav)),
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

    private async Task<string> SendAsync(string model, string json, CancellationToken ct)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new GeminiException(GeminiErrorKind.NoKey,
                "Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.");

        var url = $"{BaseUrl}{model}:generateContent";
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
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
                    Log.Info($"Gemini {model}: HTTP {(int)response.StatusCode}, request {json.Length} bytes, answer {text.Length} bytes");
                    return ExtractText(text);
                }

                var code = (int)response.StatusCode;
                if (attempt == 0 && (code is 429 or 500 or 503))
                {
                    Log.Info($"Gemini {model}: HTTP {code}, retrying once");
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }
                Log.Error($"Gemini {model}: HTTP {code} {GoogleError(text)}");
                throw MapStatus(response.StatusCode, text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Error($"Gemini {model}: timeout");
                throw new GeminiException(GeminiErrorKind.Timeout,
                    "Zeitüberschreitung: Gemini hat nicht innerhalb von 60 Sekunden geantwortet.");
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Gemini {model}: network", ex);
                throw new GeminiException(GeminiErrorKind.Network,
                    "Kein Netz: Gemini ist nicht erreichbar. Bitte Internetverbindung prüfen.", ex);
            }
        }
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
            return new GeminiException(GeminiErrorKind.Quota,
                "Kontingent erschöpft. Bitte später erneut versuchen oder das Google-Konto prüfen.");
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
