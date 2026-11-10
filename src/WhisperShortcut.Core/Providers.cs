namespace WhisperShortcut.Core;

/// <summary>One selectable model with its display name and a short hint for the settings window.</summary>
/// <param name="Id">API model id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One-line hint shown beside the name.</param>
public sealed record ModelInfo(string Id, string Name, string Description)
{
    /// <summary>Speed rank at short dictations, lower = faster; null = never featured as the fastest.</summary>
    public int? TranscriptionSpeed { get; init; }

    /// <summary>Speed rank at short edits (Dictate Prompt), lower = faster; null = never featured as the fastest.</summary>
    public int? PromptSpeed { get; init; }
}

/// <summary>Where a step (speech recognition, text processing) runs.</summary>
public enum Engine
{
    /// <summary>On this PC; nothing leaves it.</summary>
    Local,
    /// <summary>Google Gemini.</summary>
    Gemini,
    /// <summary>OpenAI.</summary>
    OpenAI,
}

/// <summary>What every engine provider can do before a request: open its connection early.</summary>
public interface IEngineProvider
{
    /// <summary>Opens the connection while the user is still speaking (cloud: TLS to the API origin; on this PC: nothing to do). Failures are ignored.</summary>
    Task PrewarmConnection();
}

/// <summary>Speech recognition: turns a WAV into raw transcript text.</summary>
public interface ISpeechProvider : IEngineProvider
{
    /// <summary>Transcribes <paramref name="wav"/>; <paramref name="glossary"/> biases spelling. Raw model text, "" if nothing was recognised.</summary>
    Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default);

    /// <summary>
    /// Like the overload above. <paramref name="sendKeywords"/> false sends no glossary keywords to a model that takes them
    /// (OpenAI gpt-transcribe, for live-meeting chunks); providers without that notion ignore it.
    /// </summary>
    Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, bool sendKeywords, CancellationToken ct = default);

    /// <summary>Cuts a recording into the pieces this provider takes per request (Gemini 2.5 min, OpenAI ~10 min, on-device 30 s).</summary>
    IReadOnlyList<byte[]> SplitForUpload(byte[] wav);
}

/// <summary>Text processing for Dictate Prompt, templates and summaries: applies a typed instruction to a selection (or composes new text).</summary>
public interface ITextProvider : IEngineProvider
{
    /// <summary>Applies <paramref name="instruction"/> to <paramref name="selection"/>; null/empty selection = compose. Raw model text.</summary>
    /// <param name="history">Earlier online edit turns to send (Mac: last 5, 5 min); null or empty = none.</param>
    Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<PromptTurn>? history = null);

    /// <summary>Like the overload above, plus an optional screenshot sent in the same user turn (a provider that cannot see ignores it).</summary>
    Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct, IReadOnlyList<PromptTurn>? history, ScreenshotImage? image);

    /// <summary>A complete <paramref name="systemPrompt"/> and user turn (templates, read-aloud rewrite); nothing from Dictate Prompt is added.</summary>
    Task<string> CompleteTextAsync(string systemPrompt, string userText, ScreenshotImage? image, CancellationToken ct = default);
}
