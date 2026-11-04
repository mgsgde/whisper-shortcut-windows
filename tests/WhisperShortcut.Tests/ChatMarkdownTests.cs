using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChatMarkdownTests
{
    [Fact]
    public void Headings_Bullets_BoldAndCode()
    {
        var spans = ChatMarkdown.Parse("## Ergebnis\n- **wichtig**: `x = 1`\n* zweiter Punkt");
        Assert.Equal("Ergebnis\n• wichtig: x = 1\n• zweiter Punkt", ChatMarkdown.PlainText(spans));
        Assert.Equal(new ChatSpan("Ergebnis\n", ChatSpanStyle.Heading), spans[0]);
        Assert.Contains(new ChatSpan("wichtig", ChatSpanStyle.Bold), spans);
        Assert.Contains(new ChatSpan("x = 1", ChatSpanStyle.Code), spans);
    }

    [Fact]
    public void FencedBlock_HidesTheFences_KeepsTheContentVerbatim()
    {
        var spans = ChatMarkdown.Parse("Hier:\n```markdown\nHallo **Anna**,\n- danke\n```\nFertig.");
        Assert.Equal("Hier:\nHallo **Anna**,\n- danke\nFertig.", ChatMarkdown.PlainText(spans));
        Assert.Contains(new ChatSpan("Hallo **Anna**,\n- danke\n", ChatSpanStyle.CodeBlock), spans);
    }

    [Fact]
    public void Links_BecomeTextWithTheAddress()
    {
        var spans = ChatMarkdown.Parse("Siehe [Doku](https://example.com/a) und [kaputt](javascript:x).");
        Assert.Equal("Siehe Doku (https://example.com/a) und [kaputt](javascript:x).", ChatMarkdown.PlainText(spans));
    }

    [Fact]
    public void NeighbouringRunsMerge_EmptyInputIsEmpty()
    {
        Assert.Empty(ChatMarkdown.Parse(""));
        Assert.Empty(ChatMarkdown.Parse(null));
        var spans = ChatMarkdown.Parse("eins\nzwei\r\ndrei");
        Assert.Single(spans);
        Assert.Equal("eins\nzwei\ndrei", spans[0].Text);
    }

    [Fact]
    public void LastCodeBlock_IsTheLastFence_EvenWhenStillOpen()
    {
        Assert.Null(ChatMarkdown.LastCodeBlock("kein Block"));
        Assert.Equal("zwei", ChatMarkdown.LastCodeBlock("```\neins\n```\ntext\n```markdown\nzwei\n```"));
        Assert.Equal("läuft noch", ChatMarkdown.LastCodeBlock("```markdown\nläuft noch"));
    }
}
