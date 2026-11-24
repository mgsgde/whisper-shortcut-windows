namespace WhisperShortcut.Core;

/// <summary>The feedback e-mail of "Problem melden" (Mac FeedbackLinks).</summary>
public static class FeedbackLinks
{
    /// <summary>E-mail address for feedback.</summary>
    public const string SupportEmail = "mgsgde@gmail.com";

    /// <summary>Subject of the feedback mail.</summary>
    public static string EmailSubject(string appVersion) => $"Feedback zu WhisperShortcut ({appVersion})";

    /// <summary>
    /// The body of the mail that <see cref="MailtoUrl"/> opens. A mailto: link holds about 2,000 characters, the report
    /// is far longer: it waits on the clipboard, and this line says where it goes. Should the user forget to paste,
    /// the line still tells the developer that the report is missing.
    /// </summary>
    public const string PasteHint = "[Hier Strg+V drücken: Der Bericht von WhisperShortcut ist in der Zwischenablage.]";

    /// <summary>The mailto link: recipient, subject and <see cref="PasteHint"/>.</summary>
    public static Uri MailtoUrl(string appVersion) =>
        new("mailto:" + SupportEmail + "?subject=" + Uri.EscapeDataString(EmailSubject(appVersion))
            + "&body=" + Uri.EscapeDataString(PasteHint));

    /// <summary><paramref name="text"/> trimmed; when longer than <paramref name="limit"/>, the first <paramref name="limit"/> characters plus "…".</summary>
    public static string Truncated(string text, int limit)
    {
        var t = (text ?? "").Trim();
        if (t.Length <= limit) return t;
        var cut = limit;
        if (cut > 0 && char.IsLowSurrogate(t[cut]) && char.IsHighSurrogate(t[cut - 1])) cut--; // never split an emoji
        return t[..cut] + "…";
    }

    /// <summary>
    /// The end of the chat as feedback context (Mac recentTranscriptForFeedback): the last 6 messages, oldest first,
    /// one line each ("Me: …" / "Assistant: …", each cut at 300 characters), the whole at most 1200. "" for no messages.
    /// </summary>
    public static string ChatExcerpt(IReadOnlyList<ChatMessage> messages)
    {
        var lines = messages.Skip(Math.Max(0, messages.Count - 6)).Select(m =>
        {
            var text = m.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            return Truncated((m.Role == ChatRole.User ? "Me: " : "Assistant: ") + text, 300);
        });
        return Truncated(string.Join("\n", lines), 1200);
    }
}
