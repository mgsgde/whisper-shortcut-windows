using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChatThinkingTests
{
    [Theory]
    [InlineData("high", "high")]
    [InlineData("  LOW ", "low")]
    [InlineData("Default", "default")]
    [InlineData("minimal", "minimal")]
    public void TryParse_accepts_levels_trimmed_and_lowercase(string input, string expected)
    {
        Assert.True(ChatThinking.TryParse(input, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("extreme")]
    public void TryParse_rejects_everything_else(string? input) => Assert.False(ChatThinking.TryParse(input, out _));

    [Theory]
    [InlineData("gemini-4-argon", null, "high")]
    [InlineData("gemini-3.1-pro", "default", "high")]
    [InlineData("gemini-3.8-flash", "", "low")]
    [InlineData("gemini-3.1-flash-lite", null, "minimal")]
    [InlineData("gemini-3.5-flash-lite", "default", "minimal")]
    [InlineData("gemini-9-new", null, "low")]
    [InlineData("gemini-3.8-flash", "bogus", "low")]
    public void Gemini_built_in_levels(string model, string? level, string expected) =>
        Assert.Equal(expected, (string?)ChatThinking.GeminiConfig(model, level)!["thinkingLevel"]);

    [Theory]
    [InlineData("gemini-3.8-flash", "minimal", "low")]
    [InlineData("gemini-4-argon", "minimal", "low")]
    [InlineData("gemini-3.1-pro", "minimal", "low")]
    [InlineData("gemini-3.5-flash-lite", "minimal", "minimal")]
    [InlineData("gemini-3.8-flash", "high", "high")]
    [InlineData("gemini-3.1-flash-lite", "medium", "medium")]
    [InlineData("gemini-4-argon", "low", "low")]
    public void Gemini_explicit_level_replaces_the_built_in(string model, string level, string expected) =>
        Assert.Equal(expected, (string?)ChatThinking.GeminiConfig(model, level)!["thinkingLevel"]);

    [Fact]
    public void Gemini_config_is_omitted_for_image_and_foreign_models()
    {
        Assert.Null(ChatThinking.GeminiConfig("gemini-3-pro-image", "high"));
        Assert.Null(ChatThinking.GeminiConfig("gpt-5", "high"));
    }

    [Theory]
    [InlineData("gpt-6-sol", null, "low")]
    [InlineData("gpt-5.4-mini", "default", "low")]
    [InlineData("gpt-4o", null, null)]
    [InlineData("gpt-6-sol", "minimal", "none")]
    [InlineData("gpt-6-sol", "low", "low")]
    [InlineData("gpt-4o", "medium", "medium")]
    [InlineData("gpt-6-luna", "high", "high")]
    public void OpenAi_effort(string model, string? level, string? expected) =>
        Assert.Equal(expected, ChatThinking.OpenAiEffort(model, level));
}
