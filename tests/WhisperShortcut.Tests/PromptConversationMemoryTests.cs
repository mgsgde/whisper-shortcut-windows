using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class PromptConversationMemoryTests
{
    [Fact]
    public void Includes_AndRecords_MatchMac()
    {
        Assert.True(PromptConversationMemory.Includes(isCompose: false, offline: false));
        Assert.False(PromptConversationMemory.Includes(isCompose: true, offline: false));
        Assert.False(PromptConversationMemory.Includes(isCompose: false, offline: true));
        Assert.True(PromptConversationMemory.Records(offline: false));
        Assert.False(PromptConversationMemory.Records(offline: true));
    }

    [Fact]
    public void Append_KeepsLastFive_AndDropsEmptyReplies()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = new PromptConversationMemory(() => now);
        m.Append("sel", "kürzer", "  ");
        Assert.Empty(m.TurnsForApi());

        for (var i = 1; i <= 6; i++)
            m.Append("s" + i, "do " + i, "r" + i);

        var turns = m.TurnsForApi();
        Assert.Equal(5, turns.Count);
        Assert.Equal("s2", turns[0].SelectedText);
        Assert.Equal("r6", turns[^1].ModelResponse);
    }

    [Fact]
    public void TurnsForApi_ExpireAfterFiveMinutes()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = new PromptConversationMemory(() => now);
        m.Append("sel", "kürzer", "kurz");
        Assert.Single(m.TurnsForApi());

        now = now.Add(PromptConversationMemory.InactivityTimeout).AddSeconds(1);
        Assert.Empty(m.TurnsForApi());
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var m = new PromptConversationMemory();
        m.Append("sel", "x", "y");
        m.Clear();
        Assert.Empty(m.TurnsForApi());
    }
}
