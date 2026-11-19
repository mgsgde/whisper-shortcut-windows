using System.Net;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Cyrillic look-alikes inside a Latin word are folded. A word with no ASCII letter is not.
/// Port of HomoglyphFoldTests (Mac #508).
/// </summary>
public class HomoglyphFoldTests
{
    [Fact]
    public void CyrillicIeInsideAGermanWordBecomesE()
    {
        // "lege" with the last letter U+0435 CYRILLIC SMALL LETTER IE.
        var input = "Ich leg\u0435 die Briefe";
        var result = TextCleanup.FoldCyrillicHomoglyphs(input);
        Assert.Equal("Ich lege die Briefe", result.Text);
        Assert.Equal(1, result.Folded);
    }

    [Fact]
    public void ASentenceWrittenOnlyInCyrillicIsUnchanged()
    {
        // "Привет мир", including look-alikes р and е. No ASCII letter, so nothing folds.
        var input = "\u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440";
        var result = TextCleanup.FoldCyrillicHomoglyphs(input);
        Assert.Equal(input, result.Text);
        Assert.Equal(0, result.Folded);
    }

    [Fact]
    public void ACyrillicWordBesideLatinWordsStaysCyrillic()
    {
        var privet = "\u041F\u0440\u0438\u0432\u0435\u0442";
        var input = "Ich sagte " + privet;
        var result = TextCleanup.FoldCyrillicHomoglyphs(input);
        Assert.Equal(input, result.Text);
        Assert.Equal(0, result.Folded);
    }

    [Fact]
    public void CyrillicCapitalsInsideALatinWordFoldToTheirLatinLookAlikes()
    {
        // В (U+0412) and е (U+0435) plus ASCII "rlin".
        var input = "\u0412\u0435rlin";
        var result = TextCleanup.FoldCyrillicHomoglyphs(input);
        Assert.Equal("Berlin", result.Text);
        Assert.Equal(2, result.Folded);
    }

    [Fact]
    public void AGermanSentenceWithUmlautsAndEszettIsUnchanged()
    {
        var input = "Für die größeren Briefe braucht Müller heißes Wasser.";
        var result = TextCleanup.FoldCyrillicHomoglyphs(input);
        Assert.Equal(input, result.Text);
        Assert.Equal(0, result.Folded);
    }

    [Fact]
    public void AnEmptyStringStaysEmpty()
    {
        var result = TextCleanup.FoldCyrillicHomoglyphs("");
        Assert.Equal("", result.Text);
        Assert.Equal(0, result.Folded);
    }

    [Fact]
    public async Task LocalDictatePromptFoldsAfterTheThinkStrip()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue((HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "<think>plan</think>Ich leg\u0435 die Briefe" }, finish_reason = "stop" } },
        })));
        var text = await new LocalLlmClient(new HttpClient(h)).CompleteAsync(
            new Uri("http://127.0.0.1:1234/"), "SYS", "USER", CancellationToken.None);
        Assert.Equal("Ich lege die Briefe", text);
    }
}
