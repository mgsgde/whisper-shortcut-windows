using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>Builds the Dictate Prompt texts; shared by the Gemini and the local path so both say the same.</summary>
public static class DictatePromptText
{
    /// <summary>The custom system prompt (trimmed) or the default, plus the output rule.</summary>
    public static string SystemPrompt(string? customSystemPrompt)
    {
        var trimmed = customSystemPrompt?.Trim();
        return (string.IsNullOrEmpty(trimmed) ? Prompts.DefaultPromptModeSystemPrompt : trimmed)
            + Prompts.PromptModeOutputRule;
    }

    /// <summary>The compose marker when nothing is selected, otherwise the selection under its header.</summary>
    public static string SelectionBlock(string? selection) =>
        string.IsNullOrEmpty(selection)
            ? Prompts.DictatePromptComposeMarker
            : $"{Prompts.ClipboardSelectionHeader}\n\n{selection}";

    /// <summary>User turn for a text-only model: selection block, then the transcribed instruction (Mac SpeechService).</summary>
    public static string LocalUserTurn(string? selection, string instruction) =>
        SelectionBlock(selection) + "\n\nVOICE INSTRUCTION:\n" + instruction;
}

/// <summary>Talks to llama-server's OpenAI-compatible chat endpoint.</summary>
public sealed class LocalLlmClient
{
    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client. There is no timeout of its own: the caller sets
    /// <c>HttpClient.Timeout = Timeout.InfiniteTimeSpan</c> and cancels through the token.
    /// </summary>
    public LocalLlmClient(HttpClient http) => _http = http;

    /// <summary>Runs one chat completion (greedy, thinking off) and returns the trimmed answer.</summary>
    public async Task<string> CompleteAsync(Uri baseUrl, string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            // Greedy like the Mac's Offline Mode: the same input should give the same output.
            ["temperature"] = 0,
            ["max_tokens"] = 4096,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };
        string text;
        try
        {
            using var resp = await _http.PostAsync(new Uri(baseUrl, "v1/chat/completions"),
                new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Status and length only: the body can echo the patient text.
                Log.Error($"local LLM HTTP {(int)resp.StatusCode}, {text.Length} chars");
                throw Failure(null);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Error("local LLM request failed", ex);
            throw Failure(ex);
        }

        try
        {
            var choice = JsonNode.Parse(text)?["choices"]?[0];
            var content = choice?["message"]?["content"]?.GetValue<string>();
            if (content == null) throw new FormatException("no content");
            if (choice?["finish_reason"]?.GetValue<string>() == "length")
            {
                // A cut-off answer would replace the user's whole selection with half a text.
                Log.Error("local LLM answer cut off at max_tokens");
                throw new LocalLlmException("Der Text ist für das Offline-Modell zu lang. Bitte einen kürzeren Abschnitt markieren.");
            }
            return Think.Replace(content, "").Trim();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error($"local LLM response unreadable ({text.Length} chars)", ex);
            throw Failure(ex);
        }
    }

    private static LocalLlmException Failure(Exception? inner) =>
        new("Das lokale Sprachmodell hat nicht geantwortet. Details im Protokoll.", inner);
}
