using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class FeedbackLinksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static ChatMessage U(string t) => new(ChatRole.User, t, T0);
    private static ChatMessage M(string t) => new(ChatRole.Model, t, T0);

    [Fact]
    public void EmailSubject_names_the_version()
    {
        Assert.Equal("Feedback zu WhisperShortcut (0.6.0)", FeedbackLinks.EmailSubject("0.6.0"));
    }

    [Fact]
    public void MailtoUrl_carries_subject_and_the_paste_hint_and_stays_short()
    {
        var mail = FeedbackLinks.MailtoUrl("0.6.0 & co").OriginalString;
        Assert.StartsWith("mailto:mgsgde@gmail.com?subject=" + Uri.EscapeDataString("Feedback zu WhisperShortcut (0.6.0 & co)") + "&body=", mail);
        Assert.EndsWith(Uri.EscapeDataString(FeedbackLinks.PasteHint), mail);
        Assert.Contains("Strg+V", FeedbackLinks.PasteHint);
        Assert.Equal(1, mail.Count(c => c == '&'));
        Assert.True(mail.Length < 1900, $"{mail.Length} chars"); // Outlook cuts links around 2,000 characters
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
    public void ChatExcerpt_is_empty_without_messages()
    {
        Assert.Equal("", FeedbackLinks.ChatExcerpt(Array.Empty<ChatMessage>()));
    }
}
