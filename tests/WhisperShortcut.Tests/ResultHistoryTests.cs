using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ResultHistoryTests
{
    private static DictationResult D(string text, int minute = 0) =>
        new(new DateTime(2026, 11, 4, 10, minute, 0, DateTimeKind.Utc), text, "Dictate", null, null, null, null, "1", null, null);

    [Fact]
    public void Keeps_ten_newest_first()
    {
        var h = new ResultHistory();
        for (var i = 0; i < 12; i++) h.AddDictation(D("t" + i, i));
        Assert.Equal(10, h.Dictations.Count);
        Assert.Equal("t11", h.Dictations[0].Text);
        Assert.Equal("t2", h.Dictations[9].Text);
    }

    [Fact]
    public void Blank_text_is_ignored() { var h = new ResultHistory(); h.AddDictation(D("  ")); Assert.Empty(h.Dictations); }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var h = new ResultHistory();
        h.AddDictation(D("a"));
        var snap = h.Dictations;
        h.AddDictation(D("b"));
        Assert.Single(snap);
    }

    [Fact]
    public void Prompt_and_read_aloud_keep_only_the_latest_and_cut_selection()
    {
        var h = new ResultHistory();
        var ts = DateTime.UtcNow;
        h.SetPrompt(new PromptResult(ts, "i1", "x", "r1", null, null, false, 0, null));
        h.SetPrompt(new PromptResult(ts, "i2", new string('s', 600), "r2", null, null, false, 0, null));
        Assert.Equal("i2", h.LastPrompt!.Instruction);
        Assert.Equal(501, h.LastPrompt.SelectedText!.Length);
        Assert.EndsWith("…", h.LastPrompt.SelectedText);
        h.SetReadAloud(new ReadAloudResult(ts, "a", null, null));
        h.SetReadAloud(new ReadAloudResult(ts, "b", null, null));
        Assert.Equal("b", h.LastReadAloud!.Text);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var h = new ResultHistory();
        h.AddDictation(D("a"));
        h.SetPrompt(new PromptResult(DateTime.UtcNow, "i", null, "r", null, null, false, 0, null));
        h.SetReadAloud(new ReadAloudResult(DateTime.UtcNow, "a", null, null));
        h.Clear();
        Assert.Empty(h.Dictations);
        Assert.Null(h.LastPrompt);
        Assert.Null(h.LastReadAloud);
    }
}
