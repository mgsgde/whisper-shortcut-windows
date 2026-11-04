using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MeetingOptionsTests
{
    [Fact]
    public void Choices_AreTheMacOnes_WithMacDefaults()
    {
        Assert.Equal(new[] { 15, 30, 45, 60 }, MeetingOptions.ChunkSeconds);
        Assert.Equal(new[] { 0, 60, 90, 120 }, MeetingOptions.SafeguardMinutes);
        Assert.Equal(60, MeetingOptions.DefaultChunkSeconds);
        Assert.Equal(90, MeetingOptions.DefaultSafeguardMinutes);
        Assert.Equal("gemini-3.5-flash-lite", MeetingOptions.DefaultSummaryModel);
        Assert.Equal(MeetingOptions.DefaultSummaryModel, GeminiModels.NormalizePrompt(MeetingOptions.DefaultSummaryModel));
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(15, 15)]
    [InlineData(45, 45)]
    [InlineData(20, 60)]
    [InlineData(0, 60)]
    public void NormalizeChunkSeconds(int? stored, int expected) => Assert.Equal(expected, MeetingOptions.NormalizeChunkSeconds(stored));

    [Theory]
    [InlineData(null, 90)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    [InlineData(30, 90)]
    public void NormalizeSafeguardMinutes(int? stored, int expected) => Assert.Equal(expected, MeetingOptions.NormalizeSafeguardMinutes(stored));

    [Fact]
    public void Labels_AreGerman()
    {
        Assert.Equal("15 Sekunden", MeetingOptions.ChunkLabel(15));
        Assert.Equal("Nie", MeetingOptions.SafeguardLabel(0));
        Assert.Equal("60 Minuten", MeetingOptions.SafeguardLabel(60));
        Assert.Equal("90 Minuten", MeetingOptions.SafeguardLabel(90));
        Assert.Equal("2 Stunden", MeetingOptions.SafeguardLabel(120));
        Assert.Equal("Dieses Meeting wird seit über 90 Minuten transkribiert. Beenden oder weitermachen?", MeetingOptions.LongMeetingText(90));
    }

    [Fact]
    public void Policy_SetsTheMaximum_KeepsTheMacPauseRules()
    {
        var p = MeetingOptions.Policy(30);
        Assert.Equal(TimeSpan.FromSeconds(30), p.MaxChunk);
        Assert.Equal(MeetingChunkPolicy.Default.MinChunk, p.MinChunk);
        Assert.Equal(MeetingChunkPolicy.Default.SilenceDuration, p.SilenceDuration);
        Assert.Equal(MeetingChunkPolicy.Default.SilenceThresholdDb, p.SilenceThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(60), MeetingOptions.Policy(60).MaxChunk);
        Assert.Equal(TimeSpan.FromSeconds(60), MeetingOptions.Policy(7).MaxChunk); // unknown → default
        Assert.True(MeetingOptions.Policy(15).MinChunk <= MeetingOptions.Policy(15).MaxChunk);
    }

    [Fact]
    public void ShouldAskLongMeeting_OnceAfterTheLimit_NeverWhenOff()
    {
        Assert.False(MeetingOptions.ShouldAskLongMeeting(90, TimeSpan.FromMinutes(89), alreadyAsked: false));
        Assert.True(MeetingOptions.ShouldAskLongMeeting(90, TimeSpan.FromMinutes(90), alreadyAsked: false));
        Assert.False(MeetingOptions.ShouldAskLongMeeting(90, TimeSpan.FromMinutes(120), alreadyAsked: true));
        Assert.False(MeetingOptions.ShouldAskLongMeeting(0, TimeSpan.FromHours(5), alreadyAsked: false));
    }

    [Fact]
    public void ChunkerHonoursTheShorterMaximum()
    {
        // 40 s of steady tone at 16 kHz: with a 15 s maximum it is cut at 15 s, 30 s and the rest on flush.
        var chunker = new MeetingChunker(policy: MeetingOptions.Policy(15));
        var tone = new short[16000 * 40];
        for (int i = 0; i < tone.Length; i++) tone[i] = (short)(8000 * Math.Sin(i * 0.05));
        var chunks = chunker.Write(tone).ToList();
        if (chunker.Flush() is { } last) chunks.Add(last);
        Assert.Equal(new[] { 15.0, 15.0, 10.0 }, chunks.Select(c => Math.Round(c.DurationSeconds)).ToArray());
    }
}
