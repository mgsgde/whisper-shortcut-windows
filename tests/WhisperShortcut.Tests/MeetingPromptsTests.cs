using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MeetingPromptsTests
{
    [Fact]
    public void Local_budget_is_derived_from_the_context_window()
    {
        Assert.Equal(16384, LocalLlm.ContextTokens);
        var expected = (LocalLlm.ContextTokens - MeetingPrompts.SummaryAnswerTokens) * MeetingPrompts.GermanCharsPerToken;
        Assert.Equal(expected, MeetingPrompts.LocalTranscriptChars);
        Assert.Equal(3, MeetingPrompts.GermanCharsPerToken);
        Assert.True(MeetingPrompts.LocalTranscriptChars < MeetingPrompts.MaxTranscriptChars);
        Assert.Equal(MeetingPrompts.MaxTranscriptChars, MeetingPrompts.BudgetChars(Engine.Gemini));
        Assert.Equal(MeetingPrompts.MaxTranscriptChars, MeetingPrompts.BudgetChars(Engine.OpenAI));
        Assert.Equal(MeetingPrompts.LocalTranscriptChars, MeetingPrompts.BudgetChars(Engine.Local));
    }

    [Fact]
    public void Clip_keeps_the_tail_and_a_smaller_budget_for_the_local_model()
    {
        var transcript = new string('ä', 100_000);
        var cloud = MeetingPrompts.Clip(transcript, Engine.Gemini);
        var local = MeetingPrompts.Clip(transcript, Engine.Local);
        Assert.Equal(MeetingPrompts.MaxTranscriptChars, cloud.Length);
        Assert.Equal(MeetingPrompts.LocalTranscriptChars, local.Length);
        Assert.Equal(transcript[^local.Length..], local);
        Assert.Equal(transcript[^cloud.Length..], cloud);
        Assert.Equal(transcript[..100], MeetingPrompts.Clip(transcript[..100], Engine.Local));
    }

    [Fact]
    public void Clip_does_not_start_on_a_low_surrogate()
    {
        var transcript = "\U0001F600" + new string('a', MeetingPrompts.MaxTranscriptChars - 1);
        var clipped = MeetingPrompts.Clip(transcript, Engine.Gemini);
        Assert.False(char.IsLowSurrogate(clipped[0]));
        Assert.StartsWith("a", clipped);
    }
}
