namespace WhisperShortcut.Core;

/// <summary>The OpenAI models offered per step, and the mapping of stored ids onto them.</summary>
public static class OpenAiModels
{
    /// <summary>Default OpenAI transcription model.</summary>
    public const string DefaultTranscriptionModel = "gpt-4o-transcribe";

    /// <summary>
    /// Pure ASR model: takes the glossary as repeated <c>keywords</c> fields and is hurt by an instruction
    /// <c>prompt</c> (Mac: isContextFieldStyle).
    /// </summary>
    public const string KeywordModel = "gpt-transcribe";

    /// <summary>True when <paramref name="id"/> takes the glossary as <c>keywords</c> instead of a prompt.</summary>
    public static bool TakesKeywords(string? id) => string.Equals(id, KeywordModel, StringComparison.Ordinal);

    /// <summary>Default OpenAI Dictate Prompt model.</summary>
    public const string DefaultPromptModel = "gpt-6-sol";

    /// <summary>Models offered for Dictate, in display order.</summary>
    public static IReadOnlyList<ModelInfo> Transcription { get; } = new[]
    {
        new ModelInfo("gpt-4o-transcribe", "GPT-4o Transcribe", "Genau, beachtet das Glossar, empfohlen") { TranscriptionSpeed = 4 },
        new ModelInfo("gpt-4o-mini-transcribe", "GPT-4o mini Transcribe", "Günstiger, etwas ungenauer") { TranscriptionSpeed = 2 },
        new ModelInfo("gpt-transcribe", "GPT Transcribe", "Neuestes Modell, Glossar als Stichwörter, ohne eigene Anweisungen") { TranscriptionSpeed = 5 },
    };

    /// <summary>Models offered for Dictate Prompt, in display order.</summary>
    public static IReadOnlyList<ModelInfo> Prompt { get; } = new[]
    {
        new ModelInfo("gpt-6-sol", "GPT-6 Sol", "Gründlich, empfohlen") { PromptSpeed = 6 },
        new ModelInfo("gpt-6-luna", "GPT-6 Luna", "Schnell und sehr günstig") { PromptSpeed = 3 },
        new ModelInfo("gpt-5.4-mini", "GPT-5.4 mini", "Günstig, einfache Aufgaben") { PromptSpeed = 4 },
    };

    // Same idea as the Mac's migrateLegacyPromptRawValue: ids that no longer exist map to a successor
    // that is still offered, so an old settings.json keeps working after an update.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["gpt-5"] = DefaultPromptModel,
        ["gpt-5.4"] = DefaultPromptModel,
        ["gpt-5.5"] = DefaultPromptModel,
        ["gpt-5.6-sol"] = DefaultPromptModel,
        ["gpt-5.6-terra"] = DefaultPromptModel,
        ["gpt-4o"] = DefaultPromptModel,
        ["gpt-5-mini"] = "gpt-5.4-mini",
        ["gpt-5.6-luna"] = "gpt-6-luna",
        ["gpt-4o-mini"] = "gpt-5.4-mini",
        ["whisper-1"] = DefaultTranscriptionModel,
    };

    /// <summary>A valid transcription model id: the id itself, its successor, or the default.</summary>
    public static string NormalizeTranscription(string? id) => Normalize(id, Transcription, DefaultTranscriptionModel);

    /// <summary>A valid Dictate Prompt model id: the id itself, its successor, or the default.</summary>
    public static string NormalizePrompt(string? id) => Normalize(id, Prompt, DefaultPromptModel);

    private static string Normalize(string? id, IReadOnlyList<ModelInfo> list, string fallback)
    {
        var t = id?.Trim();
        if (string.IsNullOrEmpty(t)) return fallback;
        if (list.Any(m => string.Equals(m.Id, t, StringComparison.Ordinal))) return t;
        if (Renamed.TryGetValue(t, out var next) && list.Any(m => m.Id == next)) return next;
        return fallback;
    }
}
