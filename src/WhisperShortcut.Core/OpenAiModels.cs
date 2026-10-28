namespace WhisperShortcut.Core;

/// <summary>The OpenAI models offered per step, and the mapping of stored ids onto them.</summary>
public static class OpenAiModels
{
    /// <summary>Default OpenAI transcription model.</summary>
    public const string DefaultTranscriptionModel = "gpt-4o-transcribe";

    /// <summary>Default OpenAI Dictate Prompt model.</summary>
    public const string DefaultPromptModel = "gpt-6-sol";

    /// <summary>Models offered for Dictate, in display order.</summary>
    public static IReadOnlyList<GeminiModel> Transcription { get; } = new[]
    {
        new GeminiModel("gpt-4o-transcribe", "GPT-4o Transcribe", "Genau, beachtet das Glossar, empfohlen"),
        new GeminiModel("gpt-4o-mini-transcribe", "GPT-4o mini Transcribe", "Günstiger, etwas ungenauer"),
        new GeminiModel("gpt-transcribe", "GPT Transcribe", "Neuestes Modell, ignoriert aber das Glossar"),
    };

    /// <summary>Models offered for Dictate Prompt, in display order.</summary>
    public static IReadOnlyList<GeminiModel> Prompt { get; } = new[]
    {
        new GeminiModel("gpt-6-sol", "GPT-6 Sol", "Gründlich, empfohlen"),
        new GeminiModel("gpt-6-luna", "GPT-6 Luna", "Schnell und sehr günstig"),
        new GeminiModel("gpt-5.4-mini", "GPT-5.4 mini", "Günstig, einfache Aufgaben"),
    };

    /// <summary>A valid transcription model id: the id itself, or the default.</summary>
    public static string NormalizeTranscription(string? id) => Normalize(id, Transcription, DefaultTranscriptionModel);

    /// <summary>A valid Dictate Prompt model id: the id itself, or the default.</summary>
    public static string NormalizePrompt(string? id) => Normalize(id, Prompt, DefaultPromptModel);

    private static string Normalize(string? id, IReadOnlyList<GeminiModel> list, string fallback)
    {
        var t = id?.Trim();
        return !string.IsNullOrEmpty(t) && list.Any(m => string.Equals(m.Id, t, StringComparison.Ordinal)) ? t : fallback;
    }
}
