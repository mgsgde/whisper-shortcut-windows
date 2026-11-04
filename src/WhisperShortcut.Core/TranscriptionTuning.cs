using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>
/// Temperature and thinking effort for Gemini transcription (Mac Diktieren → Erweitert → „Genauigkeit“,
/// TranscriptionTemperature and TranscriptionThinkingEffort). OpenAI and Parakeet take neither.
/// </summary>
public static class TranscriptionTuning
{
    /// <summary>Default temperature: 0.0, reproduces what was said (Mac default).</summary>
    public const double DefaultTemperature = 0.0;

    /// <summary>Default thinking level: minimal (Mac default; clamped to low where a model rejects it).</summary>
    public const string DefaultThinking = "minimal";

    /// <summary>The temperatures offered, with their labels (Mac: "0,0 · wörtlich", 0,2, 0,5, 1,0).</summary>
    public static readonly IReadOnlyList<(double Value, string Label)> Temperatures = new[]
    {
        (0.0, "0,0 · wörtlich"), (0.2, "0,2"), (0.5, "0,5"), (1.0, "1,0"),
    };

    /// <summary>The thinking levels offered (sent as <c>thinkingLevel</c>), with German labels.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> ThinkingLevels = new[]
    {
        ("minimal", "Minimal"), ("low", "Niedrig"), ("medium", "Mittel"), ("high", "Hoch"),
    };

    /// <summary>One of the offered temperatures (the nearest); NaN or missing = the default.</summary>
    public static double NormalizeTemperature(double? value)
    {
        if (value is not { } v || double.IsNaN(v)) return DefaultTemperature;
        return Temperatures.MinBy(t => Math.Abs(t.Value - v)).Value;
    }

    /// <summary>One of the offered thinking levels; anything else = the default.</summary>
    public static string NormalizeThinking(string? value)
    {
        var v = value?.Trim().ToLowerInvariant() ?? "";
        return ThinkingLevels.Any(t => t.Value == v) ? v : DefaultThinking;
    }

    /// <summary>The level actually sent: models that reject "minimal" (3.7 / 3.8 Flash, Pro) get "low", like the Mac.</summary>
    public static string ThinkingFor(string level, bool modelRejectsMinimal)
    {
        var l = NormalizeThinking(level);
        return l == "minimal" && modelRejectsMinimal ? "low" : l;
    }

    /// <summary>"0.2" for logs and MCP.</summary>
    public static string Format(double temperature) => temperature.ToString("0.0", CultureInfo.InvariantCulture);
}
