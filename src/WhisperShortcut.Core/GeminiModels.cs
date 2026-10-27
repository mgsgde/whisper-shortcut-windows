namespace WhisperShortcut.Core;

/// <summary>One selectable Gemini model with its display name and a short hint for the settings window.</summary>
/// <param name="Id">API model id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One-line hint shown beside the name.</param>
public sealed record GeminiModel(string Id, string Name, string Description);

/// <summary>The models offered per mode, and the mapping of stored (possibly removed) ids onto them.</summary>
public static class GeminiModels
{
    /// <summary>Models offered for Dictate, in display order.</summary>
    public static IReadOnlyList<GeminiModel> Transcription { get; } = new[]
    {
        new GeminiModel("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Schnell und günstig, für Diktate empfohlen"),
        new GeminiModel("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "Neuer, etwas teurer"),
        new GeminiModel("gemini-3.8-flash", "Gemini 3.8 Flash", "Genauer bei schwieriger Aufnahme, langsamer"),
    };

    /// <summary>Models offered for Dictate Prompt, in display order.</summary>
    public static IReadOnlyList<GeminiModel> Prompt { get; } = new[]
    {
        new GeminiModel("gemini-3.8-flash", "Gemini 3.8 Flash", "Schnell und gründlich, empfohlen"),
        new GeminiModel("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "Am schnellsten, für kurze Änderungen"),
        new GeminiModel("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Günstig, einfache Aufgaben"),
        new GeminiModel("gemini-4-argon", "Gemini 4 Argon", "Beste Texte, deutlich langsamer, nur mit bezahltem Schlüssel"),
    };

    // Same migration as the Mac app: ids that no longer exist map to their successor.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["gemini-3.7-flash"] = "gemini-3.8-flash",
        ["gemini-3.6-flash"] = "gemini-3.8-flash",
        ["gemini-3.5-flash"] = "gemini-3.8-flash",
        ["gemini-3-flash-preview"] = "gemini-3.8-flash",
        ["gemini-2.5-flash"] = "gemini-3.8-flash",
        ["gemini-2.0-flash"] = "gemini-3.8-flash",
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

    private static string Normalize(string? id, IReadOnlyList<GeminiModel> list, string fallback, bool argonAllowed)
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
