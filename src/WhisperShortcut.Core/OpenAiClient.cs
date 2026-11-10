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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Output-token ceiling for edit calls, so a long selection gets room and a cut-off reply is detectable.</summary>
    internal const int MaxCompletionTokens = 32000;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _transcriptionModel;
    private readonly Func<string> _promptModel;

    /// <summary>Spoken language for transcription (ISO 639-1 or "auto"), read on every call; "auto" sends no language field.</summary>
    public Func<string> Language { get; init; } = () => TranscriptionLanguages.Auto;

    /// <summary>Base of the exponential backoff between retries (1.5 s, doubling, capped at 15 s; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Creates a client reading key and models from <paramref name="settings"/> on every call.</summary>
    public OpenAiClient(Settings settings, HttpClient? http = null)
        : this(() => settings.OpenAiKey, () => settings.Dictate.OpenAi, () => settings.Prompt.OpenAi, http)
    {
        Language = () => settings.TranscriptionLanguage;
    }

    /// <summary>Creates a client with explicit providers (evaluated on every call).</summary>
    public OpenAiClient(Func<string?> apiKeyProvider, Func<string> transcriptionModel,
        Func<string> promptModel, HttpClient? http = null)
    {
        _apiKey = apiKeyProvider;
        _transcriptionModel = transcriptionModel;
        _promptModel = promptModel;
        _http = http ?? NetworkPolicy.CreateApiClient();
    }

    /// <inheritdoc/>
    public IReadOnlyList<byte[]> SplitForUpload(byte[] wav) => WavChunker.SplitForOpenAi(wav);

    /// <summary>Transcribes a WAV (temperature 0). Returns the raw text, "" if nothing was recognised.</summary>
    /// <param name="wav">WAV bytes.</param>
    /// <param name="glossary">Spelling reference terms; empty list sends no prompt.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default) =>
        TranscribeAsync(wav, glossary, sendKeywords: true, ct);

    /// <summary>
    /// Like the overload above. <c>gpt-transcribe</c> gets the glossary as repeated <c>keywords</c> fields
    /// (none when <paramref name="sendKeywords"/> is false, as for live-meeting chunks), no prompt and no
    /// temperature; the other models keep the instruction prompt.
    /// </summary>
    /// <param name="wav">WAV bytes.</param>
    /// <param name="glossary">Glossary lines.</param>
    /// <param name="sendKeywords">False sends no glossary to gpt-transcribe.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, bool sendKeywords, CancellationToken ct = default)
    {
        var model = _transcriptionModel();
        var keywordStyle = OpenAiModels.TakesKeywords(model);
        var keywords = keywordStyle && sendKeywords ? GlossaryPrompt.Keywords(glossary) : Array.Empty<string>();
        var hint = keywordStyle ? "" : GlossaryPrompt.Hint(glossary);
        var language = TranscriptionLanguages.ForRequest(Language());
        if (keywordStyle) Log.Info($"OpenAI transcription: {keywords.Count} keyword(s){(sendKeywords ? "" : " (keywords off)")}");
        HttpContent Build()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent(model), "model");
            if (!keywordStyle) form.Add(new StringContent("0"), "temperature");
            form.Add(new StringContent("json"), "response_format");
            if (language != null) form.Add(new StringContent(language), "language");
            foreach (var k in keywords) form.Add(new StringContent(k, Encoding.UTF8), "keywords");
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
    /// <param name="history">Earlier online edit turns (PromptConversationMemory); null = none.</param>
    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null) =>
        PromptTextAsync(selection, instruction, customSystemPrompt, ct, history, null);

    /// <summary>Like the overload without image, plus an optional screenshot sent inline in the same user message.</summary>
    /// <param name="selection">Text to edit, or null/empty for a compose turn.</param>
    /// <param name="instruction">The instruction as text.</param>
    /// <param name="customSystemPrompt">User's own system prompt; null/empty = default.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, ScreenshotImage? image, CancellationToken ct = default) =>
        PromptTextAsync(selection, instruction, customSystemPrompt, ct, null, image);

    /// <summary>Text prompt with both earlier turns and an optional screenshot (only the new turn carries the image).</summary>
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
        var json = BuildChatBody(model, selection, instruction, customSystemPrompt, history, image);
        var answer = await SendAsync("chat/completions", model,
            () => new StringContent(json, Encoding.UTF8, "application/json"), json.Length, ct).ConfigureAwait(false);
        return ExtractChatText(answer);
    }

    /// <summary>
    /// Opens the TLS session to OpenAI while the user is still speaking, so Stop does not pay the
    /// handshake on the hot path. Failures are ignored: the real request will try again.
    /// </summary>
    public Task PrewarmConnection() =>
        HttpPrewarm.Connect(_http, CloudApi.OpenAi.Origin, "OpenAI");

    /// <summary>
    /// Text-only call with a complete <paramref name="systemPrompt"/> and a complete <paramref name="userText"/>
    /// (nothing from <see cref="DictatePromptText"/> is added). Same model, reasoning effort and output limit as
    /// <see cref="PromptTextAsync"/>; a cut-off reply (finish_reason "length") throws <see cref="GeminiErrorKind.Truncated"/>.
    /// </summary>
    /// <param name="systemPrompt">The whole system message.</param>
    /// <param name="userText">The whole user message.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<string> CompleteTextAsync(string systemPrompt, string userText, CancellationToken ct = default) =>
        CompleteTextAsync(systemPrompt, userText, null, ct);

    /// <summary>Like the overload without image, plus an optional screenshot sent inline in the same user message.</summary>
    /// <param name="systemPrompt">The whole system message.</param>
    /// <param name="userText">The whole user message.</param>
    /// <param name="image">Screenshot to send along, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> CompleteTextAsync(string systemPrompt, string userText, ScreenshotImage? image, CancellationToken ct = default)
    {
        var model = _promptModel();
        var json = BuildCompleteBody(model, systemPrompt, userText, image);
        var answer = await SendAsync("chat/completions", model,
            () => new StringContent(json, Encoding.UTF8, "application/json"), json.Length, ct).ConfigureAwait(false);
        return ExtractChatText(answer);
    }

    internal static string BuildChatBody(string model, string? selection, string instruction, string? customSystemPrompt,
        IReadOnlyList<PromptTurn>? history = null, ScreenshotImage? image = null) =>
        BuildChatRequest(model, DictatePromptText.SystemPrompt(customSystemPrompt), history,
            DictatePromptText.LocalUserTurn(selection, instruction), image);

    internal static string BuildCompleteBody(string model, string systemPrompt, string userText, ScreenshotImage? image = null) =>
        BuildChatRequest(model, systemPrompt, null, userText, image);

    // Shared by Dictate Prompt and templates so both keep the same reasoning effort and output limit.
    // Only the new user message carries the screenshot; earlier turns stay text.
    private static string BuildChatRequest(string model, string systemPrompt, IReadOnlyList<PromptTurn>? history, string userText,
        ScreenshotImage? image = null)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
        };
        if (history != null)
        {
            foreach (var turn in history)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = DictatePromptText.LocalUserTurn(turn.SelectedText, turn.UserInstruction),
                });
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.ModelResponse });
            }
        }
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = image == null ? userText : UserContentWithImage(userText, image),
        });
        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            // WHY: a rewritten long selection is as long as the selection; a cut-off reply must be detectable.
            ["max_completion_tokens"] = MaxCompletionTokens,
            ["reasoning_effort"] = "low",
        }.ToJsonString();
    }

    // Content array: text, screenshot header, image (same order as the Gemini request).
    private static JsonArray UserContentWithImage(string userText, ScreenshotImage image) => new(
        new JsonObject { ["type"] = "text", ["text"] = userText },
        new JsonObject { ["type"] = "text", ["text"] = ScreenshotPrompt.Header },
        new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}",
                ["detail"] = "high",
            },
        });

    /// <summary>
    /// Checks an API key with a models-list request before it is saved. Never throws for network
    /// problems; caller cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<KeyCheck> CheckKeyAsync(string key, CancellationToken ct = default) =>
        CloudApi.OpenAi.CheckKeyAsync(_http, key, ct);

    // The body is rebuilt per attempt: a multipart content cannot be sent twice.
    private async Task<string> SendAsync(string path, string model, Func<HttpContent> buildContent, int size, CancellationToken ct)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw CloudApi.OpenAi.NoKey(KeyHint.Settings);

        return await HttpRetry.RunAsync(async attempt =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Upload time scales with the body (~40 kB/s floor, e.g. slow DSL).
            timeout.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(size / 40_000.0));
            return await CloudHttp.SendAsync(_http, CloudApi.OpenAi, CloudApi.OpenAi.ApiBase + path, key, buildContent(), $"OpenAI {model}",
                timeout.Token, ct, async response =>
                {
                    var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    Log.Info($"OpenAI {model}: HTTP {(int)response.StatusCode}, request {size} bytes, answer {text.Length} bytes, attempt {attempt}");
                    return text;
                }).ConfigureAwait(false);
        }, RetryPolicy.OneShot(RetryDelay, TimeSpan.Zero), $"OpenAI {model}", ct).ConfigureAwait(false);
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
