using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class FeedbackLinksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static ChatMessage U(string t) => new(ChatRole.User, t, T0);
    private static ChatMessage M(string t) => new(ChatRole.Model, t, T0);

    [Fact]
    public void MessageBody_without_context()
    {
        Assert.Equal("Hi! I have feedback about WhisperShortcut:\n\n---\nApp version: 0.6.0\nWindows: 11 (26100)",
            FeedbackLinks.MessageBody("  ", "0.6.0", "11 (26100)"));
        Assert.Equal(FeedbackLinks.MessageBody(null, "1", "2"), FeedbackLinks.MessageBody("", "1", "2"));
    }

    [Fact]
    public void MessageBody_with_context_is_trimmed()
    {
        Assert.Equal("Hi! I have feedback about WhisperShortcut:\n\nKontext\n\n---\nApp version: 1\nWindows: 2",
            FeedbackLinks.MessageBody("  Kontext \n", "1", "2"));
    }

    [Fact]
    public void Urls_for_the_three_channels()
    {
        var body = Uri.EscapeDataString(FeedbackLinks.MessageBody("ctx", "0.6.0", "W"));
        Assert.Equal("https://wa.me/4917641952181?text=" + body,
            FeedbackLinks.Url(FeedbackChannel.WhatsApp, "ctx", "0.6.0", "W").AbsoluteUri);
        var mail = FeedbackLinks.Url(FeedbackChannel.Email, "ctx", "0.6.0", "W").OriginalString;
        Assert.StartsWith("mailto:mgsgde@gmail.com?subject=" + Uri.EscapeDataString("WhisperShortcut feedback (0.6.0)") + "&body=", mail);
        Assert.EndsWith(body, mail);
        Assert.Equal("https://github.com/mgsgde/whisper-shortcut-windows/issues/new?body=" + body,
            FeedbackLinks.Url(FeedbackChannel.GitHub, "ctx", "0.6.0", "W").AbsoluteUri);
    }

    [Theory]
    [InlineData("  abc  ", 5, "abc")]
    [InlineData("abcdef", 3, "abc…")]
    [InlineData("abc", 3, "abc")]
    public void Truncated_cases(string text, int limit, string expected) => Assert.Equal(expected, FeedbackLinks.Truncated(text, limit));

    [Fact]
    public void ChatExcerpt_takes_the_last_six_oldest_first_on_single_lines()
    {
        var msgs = new List<ChatMessage>();
        for (var i = 1; i <= 8; i++) msgs.Add(i % 2 == 1 ? U("frage " + i) : M("antwort " + i));
        msgs[7] = M("zwei\r\nZeilen\nhier");
        var excerpt = FeedbackLinks.ChatExcerpt(msgs);
        Assert.Equal(new[]
        {
            "Me: frage 3", "Assistant: antwort 4", "Me: frage 5", "Assistant: antwort 6", "Me: frage 7",
            "Assistant: zwei  Zeilen hier",
        }, excerpt.Split('\n'));
    }

    [Fact]
    public void ChatExcerpt_limits_each_line_and_the_whole()
    {
        var one = FeedbackLinks.ChatExcerpt(new[] { U(new string('x', 500)) });
        Assert.Equal(("Me: " + new string('x', 296)) + "…", one);
        var many = Enumerable.Range(0, 6).Select(_ => U(new string('y', 500))).ToList();
        var all = FeedbackLinks.ChatExcerpt(many);
        Assert.Equal(1201, all.Length);
        Assert.EndsWith("…", all);
    }

    [Fact]
    public void ChatContext_is_null_without_messages()
    {
        Assert.Equal("", FeedbackLinks.ChatExcerpt(Array.Empty<ChatMessage>()));
        Assert.Null(FeedbackLinks.ChatContext(Array.Empty<ChatMessage>()));
        Assert.Equal("Here is the end of my chat for context:\n\nMe: hi", FeedbackLinks.ChatContext(new[] { U("hi") }));
    }
}
