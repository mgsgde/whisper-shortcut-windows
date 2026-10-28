using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class OpenAiModelsTests
{
    [Fact]
    public void Lists_ContainTheDefaults()
    {
        Assert.Contains(OpenAiModels.Transcription, m => m.Id == OpenAiModels.DefaultTranscriptionModel);
        Assert.Contains(OpenAiModels.Prompt, m => m.Id == OpenAiModels.DefaultPromptModel);
        Assert.Equal("gpt-4o-transcribe", OpenAiModels.DefaultTranscriptionModel);
        Assert.Equal("gpt-6-sol", OpenAiModels.DefaultPromptModel);
    }

    [Theory]
    [InlineData("gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe")]
    [InlineData("gpt-transcribe", "gpt-transcribe")]
    [InlineData(" gpt-4o-transcribe ", "gpt-4o-transcribe")]
    [InlineData("whisper-1", "gpt-4o-transcribe")]
    [InlineData("gemini-3.1-flash-lite", "gpt-4o-transcribe")]
    [InlineData("", "gpt-4o-transcribe")]
    [InlineData(null, "gpt-4o-transcribe")]
    public void NormalizeTranscription(string? id, string expected) =>
        Assert.Equal(expected, OpenAiModels.NormalizeTranscription(id));

    [Theory]
    [InlineData("gpt-6-luna", "gpt-6-luna")]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("gpt-4o", "gpt-6-sol")]
    [InlineData("gpt-4o-transcribe", "gpt-6-sol")]
    [InlineData(null, "gpt-6-sol")]
    public void NormalizePrompt(string? id, string expected) =>
        Assert.Equal(expected, OpenAiModels.NormalizePrompt(id));
}
