using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class GeminiModelsTests
{
    [Fact]
    public void Defaults_AreInTheirLists()
    {
        Assert.Contains(GeminiModels.Transcription, m => m.Id == Settings.DefaultTranscriptionModel);
        Assert.Contains(GeminiModels.Prompt, m => m.Id == Settings.DefaultPromptModel);
    }

    [Theory]
    [InlineData("gemini-3.1-flash-lite", "gemini-3.1-flash-lite")]
    [InlineData(" gemini-3.8-flash ", "gemini-3.8-flash")]
    [InlineData("gemini-3.7-flash", "gemini-3.8-flash")]
    [InlineData("gemini-2.5-flash", "gemini-3.8-flash")]
    [InlineData("gemini-2.0-flash-lite", "gemini-3.1-flash-lite")]
    [InlineData("gemini-2.0-flash", "gemini-3.1-flash-lite")]
    [InlineData("gemini-3.1-flash-lite-preview", "gemini-3.1-flash-lite")]
    [InlineData("gemini-4-argon", "gemini-3.1-flash-lite")]
    [InlineData("gemini-3-pro", "gemini-3.1-flash-lite")]
    [InlineData("", "gemini-3.1-flash-lite")]
    [InlineData(null, "gemini-3.1-flash-lite")]
    [InlineData("quatsch", "gemini-3.1-flash-lite")]
    public void NormalizeTranscription_Cases(string? id, string expected) =>
        Assert.Equal(expected, GeminiModels.NormalizeTranscription(id));

    [Theory]
    [InlineData("gemini-4-argon", "gemini-4-argon")]
    [InlineData("gemini-3.5-flash-lite", "gemini-3.5-flash-lite")]
    [InlineData("gemini-3.6-flash", "gemini-3.8-flash")]
    [InlineData("gemini-2.0-flash", "gemini-3.1-flash-lite")]
    [InlineData("gemini-3-flash-preview", "gemini-3.8-flash")]
    [InlineData("gemini-2.5-flash-lite", "gemini-3.1-flash-lite")]
    [InlineData("gemini-2.5-pro", "gemini-3.8-flash")]
    [InlineData(null, "gemini-3.8-flash")]
    [InlineData("  ", "gemini-3.8-flash")]
    [InlineData("quatsch", "gemini-3.8-flash")]
    public void NormalizePrompt_Cases(string? id, string expected) =>
        Assert.Equal(expected, GeminiModels.NormalizePrompt(id));

    [Fact]
    public void BuildSystemPrompt_Combinations()
    {
        var first = Prompts.DefaultPromptModeSystemPrompt.Split('\n')[0];
        Assert.Null(DictatePromptText.BuildSystemPrompt(null, null));
        Assert.Null(DictatePromptText.BuildSystemPrompt("  ", "  "));
        Assert.Equal("Eigener", DictatePromptText.BuildSystemPrompt(" Eigener ", null));

        var t = DictatePromptText.BuildSystemPrompt(null, " Vorlage X ")!;
        Assert.StartsWith(Prompts.DefaultPromptModeSystemPrompt, t);
        Assert.EndsWith("Vorlage X", t);

        var c = DictatePromptText.BuildSystemPrompt("Eigener", "Vorlage X")!;
        Assert.StartsWith("Eigener", c);
        Assert.Contains("Vorlage X", c);
        Assert.DoesNotContain(first, c);
    }

    [Theory]
    [InlineData("gemini-4-argon", true)]
    [InlineData("gemini-3.1-pro", true)]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-3.7-flash", true)]
    [InlineData("gemini-3.7-flash-preview", true)]
    [InlineData("some-PRO-model", true)]
    [InlineData("gemini-3.1-flash-lite", false)]
    [InlineData("gemini-3.5-flash-lite", false)]
    [InlineData(null, false)]
    public void RejectsMinimalThinking_isTheUnionOfExactIdsAndLegacySubstrings(string? id, bool expected) =>
        Assert.Equal(expected, GeminiModels.RejectsMinimalThinking(id));

    [Theory]
    [InlineData("gemini-4-argon", "high")]
    [InlineData("gemini-3.1-pro", "high")]
    [InlineData("gemini-3.8-flash", "low")]
    [InlineData("gemini-3.1-flash-lite", "minimal")]
    [InlineData("gemini-3.5-flash-lite", "minimal")]
    [InlineData("gemini-unknown", "low")]
    public void BuiltInThinking_values(string id, string expected) =>
        Assert.Equal(expected, GeminiModels.BuiltInThinking(id));
}
