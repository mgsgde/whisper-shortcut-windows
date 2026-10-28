namespace WhisperShortcut.Core;

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

/// <summary>Cloud speech recognition: turns a WAV into raw transcript text.</summary>
public interface ISpeechProvider
{
    /// <summary>Transcribes <paramref name="wav"/>; <paramref name="glossary"/> biases spelling. Raw model text, "" if nothing was recognised.</summary>
    Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default);
}

/// <summary>Cloud text processing for Dictate Prompt: applies a typed instruction to a selection (or composes new text).</summary>
public interface ITextProvider
{
    /// <summary>Applies <paramref name="instruction"/> to <paramref name="selection"/>; null/empty selection = compose. Raw model text.</summary>
    Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct = default);
}
