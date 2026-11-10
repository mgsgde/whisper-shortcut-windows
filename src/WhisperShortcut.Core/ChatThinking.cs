using System.Text.Json.Nodes;

namespace WhisperShortcut.Core;

/// <summary>
/// Per-chat thinking depth (<c>/think</c>, Mac ThinkingLevel): maps a level onto Gemini's
/// <c>thinkingConfig</c> and OpenAI's <c>reasoning_effort</c>.
/// </summary>
public static class ChatThinking
{
    /// <summary>The level that keeps each model's built-in behaviour.</summary>
    public const string Default = "default";

    /// <summary>All levels, lowest first, "default" last.</summary>
    public static IReadOnlyList<string> Levels { get; } = new[] { "minimal", "low", "medium", "high", "default" };

    /// <summary>The levels as shown in messages.</summary>
    public const string LevelList = "minimal | low | medium | high | default";

    /// <summary>Trims and lower-cases <paramref name="input"/>; true when the result is one of <see cref="Levels"/>.</summary>
    public static bool TryParse(string? input, out string level)
    {
        level = (input ?? "").Trim().ToLowerInvariant();
        if (Levels.Contains(level)) return true;
        level = Default;
        return false;
    }

    /// <summary>
    /// Gemini <c>generationConfig.thinkingConfig</c> for <paramref name="model"/> at <paramref name="level"/>
    /// (e.g. <c>{"thinkingLevel":"low"}</c>), or null when the field is to be omitted (image models, non-Gemini ids).
    /// </summary>
    public static JsonObject? GeminiConfig(string model, string? level)
    {
        var id = (model ?? "").Trim();
        if (!id.StartsWith("gemini-", StringComparison.Ordinal) || id.Contains("image", StringComparison.Ordinal))
            return null;

        var chosen = GeminiModels.BuiltInThinking(id);
        if (TryParse(level, out var l) && l != Default)
            chosen = l == "minimal" && GeminiModels.RejectsMinimalThinking(id) ? "low" : l;
        return new JsonObject { ["thinkingLevel"] = chosen };
    }

    /// <summary>OpenAI chat-completions <c>reasoning_effort</c> for <paramref name="model"/> at <paramref name="level"/>, or null to omit it.</summary>
    public static string? OpenAiEffort(string model, string? level)
    {
        if (!TryParse(level, out var l) || l == Default)
        {
            var id = model ?? "";
            return id.StartsWith("gpt-5", StringComparison.Ordinal) || id.StartsWith("gpt-6", StringComparison.Ordinal)
                ? "low" : null;
        }
        return l == "minimal" ? "none" : l;
    }
}
