namespace WhisperShortcut.Core;

/// <summary>The models offered per mode, and the mapping of stored (possibly removed) ids onto them.</summary>
public static class GeminiModels
{
    /// <summary>Models offered for Dictate, in display order.</summary>
    public static IReadOnlyList<ModelInfo> Transcription { get; } = new[]
    {
        new ModelInfo("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Schnell und günstig, für Diktate empfohlen") { TranscriptionSpeed = 1 },
        new ModelInfo("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "Neuer, etwas teurer") { TranscriptionSpeed = 3 },
        new ModelInfo("gemini-3.8-flash", "Gemini 3.8 Flash", "Genauer bei schwieriger Aufnahme, langsamer") { TranscriptionSpeed = 6 },
    };

    /// <summary>Models offered for Dictate Prompt, in display order.</summary>
    public static IReadOnlyList<ModelInfo> Prompt { get; } = new[]
    {
        new ModelInfo("gemini-3.8-flash", "Gemini 3.8 Flash", "Schnell und gründlich, empfohlen") { PromptSpeed = 5 },
        new ModelInfo("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "Am schnellsten, für kurze Änderungen") { PromptSpeed = 1 },
        new ModelInfo("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Günstig, einfache Aufgaben") { PromptSpeed = 2 },
        new ModelInfo("gemini-4-argon", "Gemini 4 Argon", "Beste Texte, deutlich langsamer, nur mit bezahltem Schlüssel"),
    };

    private static readonly HashSet<string> RejectsMinimalIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-4-argon", "gemini-3.1-pro", "gemini-3.8-flash",
    };

    /// <summary>True when <paramref name="id"/> answers HTTP 400 to thinkingLevel "minimal" (the caller then sends "low").</summary>
    public static bool RejectsMinimalThinking(string? id)
    {
        var t = id?.Trim() ?? "";
        // The substring rules catch legacy and unknown ids still stored in old settings.json files.
        return RejectsMinimalIds.Contains(t)
            || t.Contains("gemini-3.7-flash", StringComparison.OrdinalIgnoreCase)
            || t.Contains("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase)
            || t.Contains("-pro", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The thinkingLevel a model uses when the user keeps "default".</summary>
    public static string BuiltInThinking(string? id) => id switch
    {
        "gemini-4-argon" => "high",
        "gemini-3.1-pro" => "high",
        "gemini-3.8-flash" => "low",
        "gemini-3.1-flash-lite" or "gemini-3.5-flash-lite" => "minimal",
        _ => "low",
    };

    // Same migration as the Mac app: ids that no longer exist map to their successor.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["gemini-3.7-flash"] = "gemini-3.8-flash",
        ["gemini-3.6-flash"] = "gemini-3.8-flash",
        ["gemini-3.5-flash"] = "gemini-3.8-flash",
        ["gemini-3-flash-preview"] = "gemini-3.8-flash",
        ["gemini-2.5-flash"] = "gemini-3.8-flash",
        ["gemini-2.0-flash"] = "gemini-3.1-flash-lite",
        ["gemini-2.5-flash-lite"] = "gemini-3.1-flash-lite",
        ["gemini-2.0-flash-lite"] = "gemini-3.1-flash-lite",
        ["gemini-3.1-flash-lite-preview"] = "gemini-3.1-flash-lite",
    };

    /// <summary>A valid Dictate model id: the id itself, its successor, or the default.</summary>
    public static string NormalizeTranscription(string? id) =>
        Normalize(id, Transcription, Settings.DefaultTranscriptionModel, argonAllowed: false);

    /// <summary>A valid Dictate Prompt model id: the id itself, its successor, or the default.</summary>
    public static string NormalizePrompt(string? id) =>
        Normalize(id, Prompt, Settings.DefaultPromptModel, argonAllowed: true);

    private static string Normalize(string? id, IReadOnlyList<ModelInfo> list, string fallback, bool argonAllowed)
    {
        var t = id?.Trim();
        if (string.IsNullOrEmpty(t)) return fallback;
        if (list.Any(m => string.Equals(m.Id, t, StringComparison.Ordinal))) return t;
        if (!argonAllowed && t == "gemini-4-argon") return fallback;
        if (t.Contains("-pro", StringComparison.Ordinal)) return fallback;
        if (Renamed.TryGetValue(t, out var next) && list.Any(m => m.Id == next)) return next;
        return fallback;
    }
}
