using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class PromptHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 13, 2, 0, DateTimeKind.Utc);

    private static string TempFile(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "prompt-history.json");
    }

    private static PromptHistory Empty() => PromptHistory.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "none.json"));

    [Fact]
    public void Record_UnchangedText_StoresNothing()
    {
        var h = Empty();
        Assert.False(h.Record(PromptField.Dictate, "same", "same", T0));
        Assert.False(h.Record(PromptField.Dictate, "a\r\nb", "  a\nb \n", T0));
        Assert.False(h.Record(PromptField.Dictate, null, "", T0));
        Assert.Empty(h.Versions(PromptField.Dictate));
    }

    [Fact]
    public void Record_NewestFirst_SkipsEmptyPrevious_MovesDuplicate_CapsAtTen()
    {
        var h = Empty();
        Assert.False(h.Record(PromptField.Dictate, "", "first", T0));
        Assert.False(h.Record(PromptField.Dictate, "  \n", "first", T0));
        Assert.Empty(h.Versions(PromptField.Dictate));

        Assert.True(h.Record(PromptField.Dictate, "a", "b", T0));
        Assert.True(h.Record(PromptField.Dictate, "c", "d", T0.AddMinutes(1)));
        Assert.Equal(new[] { "c", "a" }, h.Versions(PromptField.Dictate).Select(v => v.Text));
        Assert.Equal(T0.AddMinutes(1), h.Versions(PromptField.Dictate)[0].ReplacedAt);

        // "a" is already kept: it moves to the top instead of a second copy.
        Assert.True(h.Record(PromptField.Dictate, "a\r\n", "e", T0.AddMinutes(2)));
        Assert.Equal(new[] { "a", "c" }, h.Versions(PromptField.Dictate).Select(v => v.Text));

        for (var i = 0; i < 12; i++) h.Record(PromptField.Dictate, "v" + i, "w" + i, T0.AddHours(i));
        var texts = h.Versions(PromptField.Dictate).Select(v => v.Text).ToList();
        Assert.Equal(PromptHistory.Capacity, texts.Count);
        Assert.Equal("v11", texts[0]);
        Assert.Equal("v2", texts[^1]);
    }

    [Fact]
    public void Record_RemovesEntryEqualToNewCurrent_SoABToggleWorks()
    {
        var h = Empty();
        h.Record(PromptField.PromptMode, "A", "B", T0); // saved text is B, history [A]
        Assert.Equal(new[] { "A" }, h.Versions(PromptField.PromptMode).Select(v => v.Text));

        Assert.True(h.Record(PromptField.PromptMode, "B", "A", T0.AddMinutes(1))); // restore A
        Assert.Equal(new[] { "B" }, h.Versions(PromptField.PromptMode).Select(v => v.Text));

        // Going back to empty (default) drops the matching entry but adds nothing for the empty one.
        h.Record(PromptField.Template, "X", "", T0);
        Assert.True(h.Record(PromptField.Template, "", "X", T0));
        Assert.Empty(h.Versions(PromptField.Template));
    }

    [Fact]
    public void Fields_AreIndependent()
    {
        var h = Empty();
        h.Record(PromptField.Dictate, "d", "d2", T0);
        h.Record(PromptField.Template, "t", "t2", T0);
        Assert.Single(h.Versions(PromptField.Dictate));
        Assert.Empty(h.Versions(PromptField.PromptMode));
        Assert.Single(h.Versions(PromptField.Template));
        Assert.Equal("d", h.Versions(PromptField.Dictate)[0].Text);
        Assert.Equal("t", h.Versions(PromptField.Template)[0].Text);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTextsOrderAndUtcTimes()
    {
        var file = TempFile(out var dir);
        try
        {
            var h = PromptHistory.Load(file);
            h.Record(PromptField.Dictate, "d1", "x", T0);
            h.Record(PromptField.Dictate, "d2", "y", T0.AddMinutes(5));
            h.Record(PromptField.PromptMode, "line1\r\nline2", "x", T0.AddMinutes(1));
            h.Record(PromptField.Template, "t1", "x", T0.AddMinutes(2));
            h.Save();

            var loaded = PromptHistory.Load(file);
            Assert.Equal(new[] { "d2", "d1" }, loaded.Versions(PromptField.Dictate).Select(v => v.Text));
            Assert.Equal(T0.AddMinutes(5), loaded.Versions(PromptField.Dictate)[0].ReplacedAt);
            Assert.Equal(DateTimeKind.Utc, loaded.Versions(PromptField.Dictate)[0].ReplacedAt.Kind);
            Assert.Equal("line1\nline2", loaded.Versions(PromptField.PromptMode)[0].Text);
            Assert.Equal(T0.AddMinutes(1), loaded.Versions(PromptField.PromptMode)[0].ReplacedAt);
            Assert.Equal("t1", loaded.Versions(PromptField.Template)[0].Text);
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json {")]
    public void Load_MissingEmptyOrCorruptFile_IsEmpty(string? content)
    {
        var file = TempFile(out var dir);
        try
        {
            if (content != null)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, content);
            }
            var h = PromptHistory.Load(file);
            foreach (var f in Enum.GetValues<PromptField>()) Assert.Empty(h.Versions(f));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DefaultPath_IsInSettingsFolder()
    {
        Assert.Equal("prompt-history.json", Path.GetFileName(PromptHistory.DefaultPath));
        Assert.Equal(Path.GetFullPath(AppData.SettingsFolder), Path.GetFullPath(Path.GetDirectoryName(PromptHistory.DefaultPath)!));
    }
}
