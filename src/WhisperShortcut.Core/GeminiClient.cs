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
    /// <summary>The provider account has no credit / billing set up; retrying cannot help. See <see cref="GeminiException.BillingUrl"/>.</summary>
    Billing,
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

    /// <summary>Creates a <see cref="GeminiErrorKind.Billing"/> exception that carries the page where the user can fix it.</summary>
    public GeminiException(string provider, GeminiErrorKind kind, string userMessage, string? billingUrl, Exception? inner = null)
        : this(provider, kind, userMessage, inner) => BillingUrl = billingUrl;

    /// <summary>Where to top up / enable billing (only for <see cref="GeminiErrorKind.Billing"/>), else null.</summary>
    public string? BillingUrl { get; }

    /// <summary>The error category.</summary>
    public GeminiErrorKind Kind { get; }

    /// <summary>"Gemini" or "OpenAI", for the notification title.</summary>
    public string Provider { get; }

    /// <summary>A second try can fix this (a timeout, network error, rate limit or failing server). Set by <see cref="CloudApi"/> only.</summary>
    public bool Retryable { get; internal set; }

    /// <summary>The server itself failed (5xx): such failures get one more attempt.</summary>
    public bool ServerFault { get; internal set; }

    /// <summary>How long the provider asked us to wait (RetryInfo / Retry-After), or null.</summary>
    public TimeSpan? RetryAfter { get; internal set; }
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

    /// <summary>
    /// Base of the exponential backoff between retries (1.5 s, doubling per attempt, capped at 15 s;
    /// tests set it to zero). A 429 with a Google RetryInfo delay waits that delay plus 2 s instead.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Transcription temperature (Diktieren → Erweitert), read on every call. Default 0.0.</summary>
    public Func<double> TranscriptionTemperature { get; init; } = () => TranscriptionTuning.DefaultTemperature;

    /// <summary>Transcription thinking level (Diktieren → Erweitert), read on every call. Default "minimal".</summary>
    public Func<string> TranscriptionThinking { get; init; } = () => TranscriptionTuning.DefaultThinking;

    /// <summary>Safety margin added to a Google RetryInfo delay (2 s; tests set it to zero).</summary>
    public TimeSpan RetryInfoBuffer { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Creates a client reading key and models from <paramref name="settings"/> on every call.</summary>
    public GeminiClient(Settings settings, HttpClient? http = null)
        : this(() => settings.ApiKey, () => settings.Dictate.Gemini, () => settings.Prompt.Gemini, http, () => settings.DictateSystemPrompt)
    {
        TranscriptionTemperature = () => settings.TranscriptionTemperature;
        TranscriptionThinking = () => settings.TranscriptionThinking;
    }

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

    /// <inheritdoc/>
    public Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, bool sendKeywords, CancellationToken ct = default) =>
        TranscribeAsync(wav, glossary, ct);

    /// <inheritdoc/>
    public IReadOnlyList<byte[]> SplitForUpload(byte[] wav) => WavChunker.Split(wav);

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
        var temperature = TranscriptionTuning.NormalizeTemperature(TranscriptionTemperature());
        var thinking = TranscriptionThinking();
        var body = await Task.Run(() => BuildTranscriptionBodyUtf8(model, wav, glossary, prompt, temperature, thinking), ct).ConfigureAwait(false);
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
    /// <param name="history">Earlier online edit turns (PromptConversationMemory); null = none.</param>
    /// <param name="image">Screenshot to send along in the same user turn (never stored), or null.</param>
    public async Task<string> PromptAsync(byte[] wav, string? selection, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null, ScreenshotImage? image = null)
    {
        var model = _promptModel();
        var body = await Task.Run(() => BuildPromptBodyUtf8(wav, selection, customSystemPrompt, history, image), ct).ConfigureAwait(false);
        return await SendAsync(model, body, ct, rejectTruncated: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Dictate Prompt with an optional screenshot sent inline in the same user turn (one request only, never stored).
    /// </summary>
    /// <param name="wav">WAV bytes of the spoken instruction.</param>
    /// <param name="selection">Selected text, or null/empty for a compose turn.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> PromptAsync(byte[] wav, string? selection, string? customSystemPrompt, ScreenshotImage? image, CancellationToken ct = default) =>
        PromptAsync(wav, selection, customSystemPrompt, ct, null, image);

    /// <summary>
    /// Dictate Prompt without audio: applies the typed <paramref name="instruction"/> to
    /// <paramref name="selection"/> (or composes new text when null/empty). Same model, thinking level
    /// and system prompt as <see cref="PromptAsync"/>. Returns the raw model text.
    /// </summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text (a quick action, optionally with spoken additions).</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="history">Earlier online edit turns (PromptConversationMemory); null = none.</param>
    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null) =>
        PromptTextAsync(selection, instruction, customSystemPrompt, ct, history, null);

    /// <summary>Like the overload without image, plus an optional screenshot sent inline in the same user turn.</summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, ScreenshotImage? image, CancellationToken ct = default) =>
        PromptTextAsync(selection, instruction, customSystemPrompt, ct, null, image);

    /// <summary>Text prompt with both earlier turns and an optional screenshot (the new turn carries the image).</summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="history">Earlier online edit turns; null = none.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    public async Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct, IReadOnlyList<PromptTurn>? history, ScreenshotImage? image)
    {
        var model = _promptModel();
        var body = image == null
            ? BuildPromptTextBodyUtf8(selection, instruction, customSystemPrompt, history)
            : await Task.Run(() => BuildPromptTextBodyUtf8(selection, instruction, customSystemPrompt, history, image), ct).ConfigureAwait(false);
        return await SendAsync(model, body, ct, rejectTruncated: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the TLS session to Gemini while the user is still speaking, so Stop does not pay the
    /// handshake on the hot path. Failures are ignored: the real request will try again.
    /// </summary>
    public Task PrewarmConnection() =>
        HttpPrewarm.Connect(_http, CloudApi.Gemini.Origin, "Gemini");

    /// <summary>
    /// Text-only call with a complete <paramref name="systemPrompt"/> and a complete <paramref name="userText"/>
    /// (nothing from <see cref="DictatePromptText"/> is added). Same model, thinking level and output limit as
    /// <see cref="PromptTextAsync"/>; a cut-off reply throws <see cref="GeminiErrorKind.Truncated"/>. Returns the raw model text.
    /// </summary>
    /// <param name="systemPrompt">The whole system instruction.</param>
    /// <param name="userText">The whole user turn.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> CompleteTextAsync(string systemPrompt, string userText, CancellationToken ct = default) =>
        CompleteTextAsync(systemPrompt, userText, null, ct);

    /// <summary>Like the overload without image, plus an optional screenshot sent inline in the same user turn.</summary>
    /// <param name="systemPrompt">The whole system instruction.</param>
    /// <param name="userText">The whole user turn.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> CompleteTextAsync(string systemPrompt, string userText, ScreenshotImage? image, CancellationToken ct = default)
    {
        var model = _promptModel();
        var body = image == null
            ? BuildCompleteBodyUtf8(systemPrompt, userText)
            : await Task.Run(() => BuildCompleteBodyUtf8(systemPrompt, userText, image), ct).ConfigureAwait(false);
        return await SendAsync(model, body, ct, rejectTruncated: true).ConfigureAwait(false);
    }

    internal static string BuildCompleteBody(string systemPrompt, string userText, ScreenshotImage? image = null) =>
        Encoding.UTF8.GetString(BuildCompleteBodyUtf8(systemPrompt, userText, image));

    internal static string BuildTranscriptionBody(string model, byte[] wav, IReadOnlyList<string> glossary, string? customPrompt = null,
        double temperature = TranscriptionTuning.DefaultTemperature, string thinking = TranscriptionTuning.DefaultThinking) =>
        Encoding.UTF8.GetString(BuildTranscriptionBodyUtf8(model, wav, glossary, customPrompt, temperature, thinking));

    internal static string BuildPromptBody(byte[] wav, string? selection, string? customSystemPrompt, ScreenshotImage? image = null) =>
        Encoding.UTF8.GetString(BuildPromptBodyUtf8(wav, selection, customSystemPrompt, null, image));

    internal static string BuildPromptTextBody(string? selection, string instruction, string? customSystemPrompt, ScreenshotImage? image = null) =>
        Encoding.UTF8.GetString(BuildPromptTextBodyUtf8(selection, instruction, customSystemPrompt, null, image));

    /// <summary>
    /// Writes the generateContent body with <see cref="Utf8JsonWriter.WriteBase64String(string, ReadOnlySpan{byte})"/>
    /// so a 2-minute chunk is not first turned into a base64 string, then a JsonObject, then a JSON string.
    /// </summary>
    internal static byte[] BuildTranscriptionBodyUtf8(string model, byte[] wav, IReadOnlyList<string> glossary, string? customPrompt = null,
        double temperature = TranscriptionTuning.DefaultTemperature, string thinking = TranscriptionTuning.DefaultThinking)
    {
        var instruction = string.IsNullOrWhiteSpace(customPrompt) ? Prompts.DefaultTranscriptionSystemPrompt : customPrompt.Trim();
        // Notes like `Bachata (not "Batchata")` become tie-breakers; the rejected spelling never goes out as vocabulary.
        var hint = GlossaryPrompt.Hint(glossary);
        if (hint.Length > 0) instruction += "\n\n" + hint;

        // 3.7 / 3.8 Flash and Pro reject "minimal" (HTTP 400): clamp to "low" like TranscriptionModels.swift.
        var level = TranscriptionTuning.ThinkingFor(thinking, GeminiModels.RejectsMinimalThinking(model));
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
            // Like TranscriptionModels.swift: 0.0 by default, no sampling noise in a transcript (Diktieren → Erweitert).
            w.WriteNumber("temperature", temperature);
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
        IReadOnlyList<PromptTurn>? history = null, ScreenshotImage? image = null)
    {
        var system = DictatePromptText.SystemPrompt(customSystemPrompt);
        var selectionText = DictatePromptText.SelectionBlock(selection);
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(wav.Length + ImageBytes(image), system.Length + selectionText.Length + HistoryChars(history)));
        using (var w = new Utf8JsonWriter(writer))
        {
            // Part order: text, screenshot header, screenshot, audio.
            WritePromptEnvelope(w, system, history, () =>
            {
                w.WriteStartObject();
                w.WriteString("text", selectionText);
                w.WriteEndObject();
                WriteImageParts(w, image);
                WriteAudioPart(w, wav);
            });
        }
        return writer.WrittenSpan.ToArray();
    }

    internal static byte[] BuildPromptTextBodyUtf8(string? selection, string instruction, string? customSystemPrompt,
        IReadOnlyList<PromptTurn>? history = null, ScreenshotImage? image = null)
    {
        var system = DictatePromptText.SystemPrompt(customSystemPrompt);
        var user = DictatePromptText.LocalUserTurn(selection, instruction);
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(ImageBytes(image), system.Length + user.Length + HistoryChars(history)));
        using (var w = new Utf8JsonWriter(writer))
        {
            WritePromptEnvelope(w, system, history, () =>
            {
                w.WriteStartObject();
                w.WriteString("text", user);
                w.WriteEndObject();
                WriteImageParts(w, image);
            });
        }
        return writer.WrittenSpan.ToArray();
    }

    // Templates: the whole system prompt and the whole user turn come from the caller; no history.
    internal static byte[] BuildCompleteBodyUtf8(string systemPrompt, string userText, ScreenshotImage? image = null)
    {
        var writer = new ArrayBufferWriter<byte>(JsonCapacity(ImageBytes(image), systemPrompt.Length + userText.Length));
        using (var w = new Utf8JsonWriter(writer))
        {
            WritePromptEnvelope(w, systemPrompt, null, () =>
            {
                w.WriteStartObject();
                w.WriteString("text", userText);
                w.WriteEndObject();
                WriteImageParts(w, image);
            });
        }
        return writer.WrittenSpan.ToArray();
    }

    // Screenshot header + inline image (sent once, never stored); nothing when there is no image.
    private static void WriteImageParts(Utf8JsonWriter w, ScreenshotImage? image)
    {
        if (image == null) return;
        w.WriteStartObject();
        w.WriteString("text", ScreenshotPrompt.Header);
        w.WriteEndObject();
        w.WriteStartObject();
        w.WritePropertyName("inline_data");
        w.WriteStartObject();
        w.WriteString("mime_type", image.MimeType);
        w.WriteBase64String("data", image.Data);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static int ImageBytes(ScreenshotImage? image) =>
        image == null ? 0 : image.Data.Length + ScreenshotPrompt.Header.Length * 3;

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

    /// <summary>
    /// Checks an API key with a cheap models-list request before it is saved. Never throws for
    /// network problems; caller cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<KeyCheck> CheckKeyAsync(string key, CancellationToken ct = default) =>
        CloudApi.Gemini.CheckKeyAsync(_http, key, ct);

    private static ByteArrayContent JsonUtf8Content(byte[] json)
    {
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<string> SendAsync(string model, byte[] json, CancellationToken ct, bool rejectTruncated = false)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw CloudApi.Gemini.NoKey(KeyHint.Settings);

        var url = $"{CloudApi.Gemini.ApiBase}{model}:generateContent";
        return await HttpRetry.RunAsync(async attempt =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Upload time scales with the body (~40 kB/s floor, e.g. slow DSL).
            timeout.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(json.Length / 40_000.0));
            return await CloudHttp.SendAsync(_http, CloudApi.Gemini, url, key, JsonUtf8Content(json), $"Gemini {model}",
                timeout.Token, ct, async response =>
                {
                    var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    Log.Info($"Gemini {model}: HTTP {(int)response.StatusCode}, request {json.Length} bytes, answer {text.Length} bytes, attempt {attempt}");
                    return ExtractText(text, rejectTruncated);
                }).ConfigureAwait(false);
        }, RetryPolicy.OneShot(RetryDelay, RetryInfoBuffer), $"Gemini {model}", ct).ConfigureAwait(false);
    }

    /// <summary>The delay from a Google RetryInfo detail ("55s", "1.5s"), or null when absent.</summary>
    public static TimeSpan? ParseRetryInfo(string body) => CloudApi.ParseRetryInfo(body);

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
