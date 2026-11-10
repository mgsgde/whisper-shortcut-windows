using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>Where a "no key" message sends the user: the settings window in general, or Einstellungen → Konten (Read Aloud).</summary>
internal enum KeyHint
{
    /// <summary>"... Bitte in den Einstellungen eintragen."</summary>
    Settings,
    /// <summary>"... Bitte unter Einstellungen → Konten eintragen."</summary>
    Accounts,
}

/// <summary>
/// The wire profile of one cloud provider: where it lives, how a request is authorized, how a failed answer maps to a
/// <see cref="GeminiException"/> (German text, retry decision, wait hint) and how its key is checked. Dictation, chat and
/// read aloud all talk to the same two providers, so this is the one place that knows the difference.
/// </summary>
internal sealed class CloudApi
{
    /// <summary>Google Gemini (generativelanguage.googleapis.com, key in the x-goog-api-key header).</summary>
    public static readonly CloudApi Gemini = new(true);

    /// <summary>OpenAI (api.openai.com, Bearer token).</summary>
    public static readonly CloudApi OpenAi = new(false);

    private readonly bool _gemini;

    private CloudApi(bool gemini)
    {
        _gemini = gemini;
        Name = gemini ? "Gemini" : "OpenAI";
        Origin = gemini ? "https://generativelanguage.googleapis.com/" : "https://api.openai.com/";
        ApiBase = gemini ? Origin + "v1beta/models/" : Origin + "v1/";
    }

    /// <summary>"Gemini" or "OpenAI": <see cref="GeminiException.Provider"/> and the name in user messages.</summary>
    public string Name { get; }

    /// <summary>Scheme and host with a trailing slash (what the connection prewarm opens).</summary>
    public string Origin { get; }

    /// <summary>Gemini: the models collection; OpenAI: the v1 root. Request paths are appended.</summary>
    public string ApiBase { get; }

    /// <summary>Puts the key where the provider expects it (never in the URL, so it cannot reach proxy or server logs).</summary>
    public void Authorize(HttpRequestMessage request, string key)
    {
        if (_gemini) request.Headers.Add("x-goog-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>The "no key entered" failure, worded for the place that raised it.</summary>
    public GeminiException NoKey(KeyHint hint) => new(Name, GeminiErrorKind.NoKey, hint switch
    {
        KeyHint.Accounts => $"Kein {Name}-API-Schlüssel eingetragen. Bitte unter Einstellungen → Konten eintragen.",
        _ => _gemini
            ? "Kein API-Schlüssel eingetragen. Bitte in den Einstellungen eintragen."
            : "Kein OpenAI-Schlüssel eingetragen. Bitte in den Einstellungen eintragen.",
    });

    /// <summary>No answer in time. Retryable: the next try may be quicker.</summary>
    public GeminiException Timeout() => new(Name, GeminiErrorKind.Timeout,
        $"Zeitüberschreitung: {Name} hat nicht rechtzeitig geantwortet. Bitte Internetverbindung prüfen.")
    { Retryable = true };

    /// <summary>Host unreachable or the connection broke. Retryable.</summary>
    public GeminiException Network(Exception inner) => new(Name, GeminiErrorKind.Network,
        $"Kein Netz: {Name} ist nicht erreichbar. Bitte Internetverbindung prüfen.", inner)
    { Retryable = true };

    /// <summary>
    /// Maps a non-2xx answer. <see cref="GeminiException.Retryable"/> is set for the statuses a second try can fix (429, 5xx),
    /// except a spent daily quota or an empty account; <see cref="GeminiException.RetryAfter"/> carries the provider's own
    /// wait hint on a 429 (Gemini: RetryInfo in the body, OpenAI: Retry-After header).
    /// </summary>
    /// <param name="status">HTTP status (for an error object inside a 200 stream: its code).</param>
    /// <param name="body">Response body, or the stream object that held the error.</param>
    /// <param name="headers">Response headers; null when the error did not come with a response.</param>
    public GeminiException MapHttpError(HttpStatusCode status, string body, HttpResponseHeaders? headers)
    {
        var code = (int)status;
        var ex = _gemini ? MapGemini(code, body) : MapOpenAi(code, OpenAiErrorCode(body));
        // A daily quota will not recover within seconds, an empty account (Billing) never within the hour.
        var daily = code == 429 && _gemini && IsDailyQuota(body);
        ex.Retryable = HttpRetry.IsTransientStatus(code) && !daily && (ex.Kind is GeminiErrorKind.Quota or GeminiErrorKind.Server);
        ex.ServerFault = code >= 500;
        if (code == 429) ex.RetryAfter = _gemini ? ParseRetryInfo(body) : headers == null ? null : ParseRetryAfter(headers);
        return ex;
    }

    /// <summary>
    /// What of an error body may go into the log: only the status word (Gemini, e.g. RESOURCE_EXHAUSTED) or error.code /
    /// error.type (OpenAI), never the message, which can quote request content.
    /// </summary>
    public string LogStatus(string body) => _gemini ? GoogleStatus(body) : OpenAiErrorCode(body);

    /// <summary>
    /// An error inside a 200 stream object, or (Gemini) a blocked prompt; null when the object has neither. Gemini accepts any
    /// non-null <c>error</c> and reads the HTTP-like code from it, OpenAI only an object (500 then). A blocked prompt is not
    /// retryable: the same text is blocked again.
    /// </summary>
    /// <param name="root">The parsed stream object.</param>
    /// <param name="json">Its raw text (the error mapper reads the body).</param>
    /// <param name="logTag">Prefix of the log line, e.g. "Chat Gemini".</param>
    /// <param name="blockedWhat">What was rejected, for the message: "die Anfrage", "den Text zum Vorlesen".</param>
    public GeminiException? StreamError(JsonElement root, string json, string logTag, string blockedWhat)
    {
        if (!root.TryGetProperty("error", out var err)) err = default;
        if (_gemini)
        {
            if (err.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                // The HTTP status was 200, so the code lives in the error object.
                var code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c)
                    && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n is >= 400 and <= 599 ? n : 500;
                Log.Error($"{logTag}: stream error object {code} {LogStatus(json)}");
                return MapHttpError((HttpStatusCode)code, json, null);
            }
            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object
                && feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String)
                return new GeminiException(Name, GeminiErrorKind.Server, $"Gemini hat {blockedWhat} abgelehnt ({block.GetString()}).");
            return null;
        }
        if (err.ValueKind != JsonValueKind.Object) return null;
        Log.Error($"{logTag}: stream error object {LogStatus(json)}");
        return MapHttpError(HttpStatusCode.InternalServerError, json, null);
    }

    /// <summary>
    /// Checks an API key with a cheap models-list request before it is saved. Never throws for network problems; caller
    /// cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<KeyCheck> CheckKeyAsync(HttpClient http, string key, CancellationToken ct)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return KeyCheck.Invalid;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        KeyCheck result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _gemini ? ApiBase + "?pageSize=1" : ApiBase + "models");
            Authorize(request, key);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            result = (int)response.StatusCode switch
            {
                200 => KeyCheck.Ok,
                400 when _gemini => KeyCheck.Invalid,
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
        Log.Info($"{Name} key check: {result}");
        return result;
    }

    // ---- Gemini ----

    private static GeminiException MapGemini(int code, string body)
    {
        if (body.Contains("SERVICE_DISABLED") || body.Contains("has not been used in project"))
            return new GeminiException(GeminiErrorKind.InvalidKey,
                "Die Gemini-API ist im Google-Projekt dieses Schlüssels nicht aktiviert (Generative Language API).");
        if (code == 400 && IsBillingProblem(body))
            return new GeminiException("Gemini", GeminiErrorKind.Billing,
                "Für diesen Gemini-Schlüssel ist keine Abrechnung eingerichtet.", "https://aistudio.google.com/app/apikey");
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

    /// <summary>True when a 429 body says the per-day quota is used up (retrying within the minute is pointless).</summary>
    internal static bool IsDailyQuota(string body) =>
        body.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
        || body.Contains("per day", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A 400 body with status FAILED_PRECONDITION, or a message naming billing / free tier / payment (as on the Mac).
    /// Not "User location is not supported", which is also FAILED_PRECONDITION but no billing problem.
    /// </summary>
    private static bool IsBillingProblem(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object) return false;
            if (e.TryGetProperty("message", out var lm) && lm.ValueKind == JsonValueKind.String
                && (lm.GetString() ?? "").Contains("location", StringComparison.OrdinalIgnoreCase)) return false;
            if (e.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                && st.GetString() == "FAILED_PRECONDITION") return true;
            if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString() ?? "";
                return msg.Contains("billing", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("free tier", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("payment", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Only the status word (e.g. RESOURCE_EXHAUSTED) of a Google error body: the message can quote request content.</summary>
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

    /// <summary>The delay from a Google RetryInfo detail ("55s", "1.5s"), or null when absent.</summary>
    internal static TimeSpan? ParseRetryInfo(string body)
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
                if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0 && secs < 86_400)
                    return TimeSpan.FromSeconds(secs);
            }
        }
        catch (JsonException) { }
        return null;
    }

    // ---- OpenAI ----

    private static GeminiException MapOpenAi(int code, string errorCode)
    {
        if (code == 401)
            return new GeminiException("OpenAI", GeminiErrorKind.InvalidKey, "OpenAI-Schlüssel ungültig. Bitte in den Einstellungen prüfen.");
        if (code == 403)
            return new GeminiException("OpenAI", GeminiErrorKind.InvalidKey,
                "Dieser OpenAI-Schlüssel darf das Modell nicht benutzen (Projekt- oder Organisationsrechte prüfen).");
        if (code == 429 && errorCode == "insufficient_quota")
            return new GeminiException("OpenAI", GeminiErrorKind.Billing, "Das OpenAI-Konto hat kein Guthaben mehr.",
                "https://platform.openai.com/settings/organization/billing");
        if (code == 429)
            return new GeminiException("OpenAI", GeminiErrorKind.Quota,
                "OpenAI ist gerade ausgelastet (zu viele Anfragen). Bitte in einer Minute noch einmal versuchen.");
        if (code == 404)
            return new GeminiException("OpenAI", GeminiErrorKind.Server, UserCopy.ModelGone("OpenAI"));
        return new GeminiException("OpenAI", GeminiErrorKind.Server, UserCopy.ServerBusy("OpenAI"));
    }

    /// <summary>error.code, else error.type, of an OpenAI error body ("insufficient_quota" wins if either has it); "" when absent. Never the message (it can quote content).</summary>
    internal static string OpenAiErrorCode(string body)
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
}

/// <summary>Sends one request to a <see cref="CloudApi"/> and turns every way it can fail into a <see cref="GeminiException"/>.</summary>
internal static class CloudHttp
{
    /// <summary>
    /// POSTs <paramref name="content"/> (ExpectContinue off, key set by the provider profile). A non-2xx answer is read, logged
    /// (status word only) and thrown as the mapped <see cref="GeminiException"/>; otherwise <paramref name="onSuccess"/> reads
    /// the answer. Running it inside this guard means a stall or a dropped connection while reading maps like one on connect.
    /// </summary>
    /// <param name="http">The client.</param>
    /// <param name="api">The provider.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="key">API key (already trimmed, non-empty).</param>
    /// <param name="content">Request body; owned (disposed) by the request.</param>
    /// <param name="logTag">Prefix of the log lines, e.g. "Gemini gemini-3.1-flash".</param>
    /// <param name="token">Timeout/watchdog token that every await uses.</param>
    /// <param name="caller">The caller's own token: only its cancellation passes through unmapped.</param>
    /// <param name="onSuccess">Reads the 2xx response (still open).</param>
    /// <param name="completion">ResponseHeadersRead for streams.</param>
    /// <param name="timeoutNote">What the log says when <paramref name="token"/> fired.</param>
    public static async Task<T> SendAsync<T>(HttpClient http, CloudApi api, string url, string key, HttpContent content,
        string logTag, CancellationToken token, CancellationToken caller, Func<HttpResponseMessage, Task<T>> onSuccess,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, string timeoutNote = "timeout")
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.ExpectContinue = false;
            api.Authorize(request, key);
            using var response = await http.SendAsync(request, completion, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                Log.Error($"{logTag}: HTTP {(int)response.StatusCode} {api.LogStatus(body)}");
                throw api.MapHttpError(response.StatusCode, body, response.Headers);
            }
            return await onSuccess(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            Log.Error($"{logTag}: {timeoutNote}");
            throw api.Timeout();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Error($"{logTag}: network", ex);
            throw api.Network(ex);
        }
    }
}
