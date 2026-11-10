using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;
using WhisperShortcut.Tests.Mcp;

namespace WhisperShortcut.Tests;

public class ChoicesTests
{
    private static void RoundTrips<T>(ChoiceSet<T> set, T fallback, string upperToken)
    {
        Assert.NotEmpty(set.All);
        foreach (var c in set.All)
        {
            Assert.Equal(c.Token, set.Token(c.Value));
            Assert.Equal(c.Value, set.Parse(set.Token(c.Value)));
            Assert.Equal(c.Value, set.Parse("  " + c.Token.ToUpperInvariant() + " "));
            Assert.False(string.IsNullOrWhiteSpace(set.Label(c.Value)));
        }
        Assert.Equal(set.All.Count, set.All.Select(c => c.Token).Distinct().Count());
        Assert.Equal(fallback, set.Parse(null));
        Assert.Equal(fallback, set.Parse(""));
        Assert.Equal(fallback, set.Parse("nonsense"));
        Assert.False(set.TryParse("nonsense", out _));
        Assert.True(set.TryParse(upperToken, out _));
    }

    [Fact]
    public void EveryChoiceSet_RoundTripsAndIsLenient()
    {
        RoundTrips(Choices.Engine, Engine.Gemini, "OPENAI");
        RoundTrips(Choices.Punctuation, PunctuationMode.Automatic, "spokenonly");
        RoundTrips(Choices.DictateMode, DictateMode.Dictate, "LIVE");
        RoundTrips(Choices.AfterPaste, ClipboardAfterPaste.KeepDictation, " Restore ");
    }

    [Fact]
    public void StoredTokens_StayAsBefore()
    {
        Assert.Equal(new[] { "gemini", "openai", "local" }, Choices.Engine.All.Select(c => c.Token));
        Assert.Equal(new[] { "automatic", "spokenOnly" }, Choices.Punctuation.All.Select(c => c.Token));
        Assert.Equal(new[] { "dictate", "live" }, Choices.DictateMode.All.Select(c => c.Token));
        Assert.Equal(new[] { "keep", "restore", "clear" }, Choices.AfterPaste.All.Select(c => c.Token));
    }

    [Fact]
    public void AfterPaste_LabelsExplanationsAndLegacyFallback()
    {
        foreach (var c in Choices.AfterPaste.All)
            Assert.DoesNotContain("Sie ", Choices.AfterPaste.Explanation(c.Value)); // du-form
        Assert.Equal("Diktat behalten", Choices.AfterPaste.Label(ClipboardAfterPaste.KeepDictation));
        Assert.False(Choices.AfterPaste.TryParse(null, out _));
        Assert.Equal(ClipboardAfterPaste.Clear, Choices.AfterPasteFromFile("clear", true));
        Assert.Equal(ClipboardAfterPaste.RestorePrevious, Choices.AfterPasteFromFile(null, null));
        Assert.Equal(ClipboardAfterPaste.KeepDictation, Choices.AfterPasteFromFile("x", false));
    }

    [Fact]
    public void McpOptionLabels_EqualTheChoiceSetLabels()
    {
        using var s = new McpStack();
        IReadOnlyList<McpSettingOption> Options(string id) => s.Catalog.First(x => x.Id == id).Options!();

        void Check<T>(string id, ChoiceSet<T> set) =>
            Assert.Equal(set.All.Select(c => (c.Token, c.Label)), Options(id).Select(o => (o.Value, o.Label)));

        Check("dictation.punctuation", Choices.Punctuation);
        Check("dictation.mode", Choices.DictateMode);
        Check("clipboard.after_paste", Choices.AfterPaste);

        foreach (var id in new[] { "dictation.engine", "dictate_prompt.engine" })
        {
            var opts = Options(id);
            foreach (var e in new[] { Engine.Gemini, Engine.OpenAI })
                Assert.Equal(Choices.Engine.Label(e), opts.First(o => o.Value == Choices.Engine.Token(e)).Label);
            Assert.Contains(opts, o => o.Value == "local");
        }
    }
}
