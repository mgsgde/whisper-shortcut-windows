using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class OpenAiModelsTests
{
    [Fact]
    public void Lists_ContainTheDefaults()
    {
        Assert.Contains(OpenAiModels.Transcription, m => m.Id == OpenAiModels.DefaultTranscriptionModel);
        Assert.Contains(OpenAiModels.Prompt, m => m.Id == OpenAiModels.DefaultPromptModel);
        Assert.Equal("gpt-transcribe", OpenAiModels.DefaultTranscriptionModel);
        Assert.Equal("gpt-6-sol", OpenAiModels.DefaultPromptModel);
        Assert.DoesNotContain(OpenAiModels.Transcription, m => m.Id == OpenAiModels.RetiredTranscriptionModel);
        Assert.Contains(OpenAiModels.Transcription, m => m.Id == "gpt-4o-mini-transcribe");
    }

    [Theory]
    [InlineData("gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe")]
    [InlineData("gpt-transcribe", "gpt-transcribe")]
    [InlineData(" gpt-4o-transcribe ", "gpt-transcribe")]
    [InlineData("gpt-4o-transcribe", "gpt-transcribe")]
    [InlineData("whisper-1", "gpt-transcribe")]
    [InlineData("gemini-3.1-flash-lite", "gpt-transcribe")]
    [InlineData("", "gpt-transcribe")]
    [InlineData(null, "gpt-transcribe")]
    public void NormalizeTranscription(string? id, string expected) =>
        Assert.Equal(expected, OpenAiModels.NormalizeTranscription(id));

    [Theory]
    [InlineData("gpt-6-luna", "gpt-6-luna")]
    [InlineData("gpt-6.1-terra", "gpt-6.1-terra")]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("gpt-4o", "gpt-6-sol")]
    [InlineData("gpt-5", "gpt-6-sol")]
    [InlineData("gpt-5.6-sol", "gpt-6-sol")]
    [InlineData("gpt-5-mini", "gpt-5.4-mini")]
    [InlineData("gpt-5.6-luna", "gpt-6-luna")]
    [InlineData("gpt-4o-transcribe", "gpt-6-sol")]
    [InlineData(null, "gpt-6-sol")]
    public void NormalizePrompt(string? id, string expected) =>
        Assert.Equal(expected, OpenAiModels.NormalizePrompt(id));

    [Fact]
    public void Prompt_catalog_lists_Terra_between_Sol_and_Luna()
    {
        Assert.Equal(new[] { "gpt-6-sol", "gpt-6.1-terra", "gpt-6-luna", "gpt-5.4-mini" }, OpenAiModels.Prompt.Select(m => m.Id));
        Assert.Equal("GPT-6.1 Terra", OpenAiModels.Prompt.Single(m => m.Id == "gpt-6.1-terra").Name);
    }
}
