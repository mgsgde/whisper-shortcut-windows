namespace WhisperShortcut.Core;

/// <summary>The feedback e-mail of "Problem melden" (Mac FeedbackLinks).</summary>
public static class FeedbackLinks
{
    /// <summary>E-mail address for feedback.</summary>
    public const string SupportEmail = "mgsgde@gmail.com";

    /// <summary>The context is cut to this many characters before the mailto link is built (Mac).</summary>
    public const int MailtoContextLimit = 6000;

    /// <summary>Subject of the feedback mail.</summary>
    public static string EmailSubject(string appVersion) => $"Feedback zu WhisperShortcut ({appVersion})";

    /// <summary>The message: greeting, the optional <paramref name="context"/>, then app and Windows version.</summary>
    public static string MessageBody(string? context, string appVersion, string windowsVersion)
    {
        var text = "Hallo, ich habe Feedback zu WhisperShortcut:";
        if (!string.IsNullOrWhiteSpace(context)) text += "\n\n" + SecretRedactor.Redact(context.Trim()); // never a key in the mail
        return text + $"\n\n---\nApp-Version: {appVersion}\nWindows: {windowsVersion}";
    }

    /// <summary>The mailto link with subject and <see cref="MessageBody"/> filled in.</summary>
    public static Uri MailtoUrl(string? context, string appVersion, string windowsVersion) =>
        new("mailto:" + SupportEmail + "?subject=" + Uri.EscapeDataString(EmailSubject(appVersion))
            + "&body=" + Uri.EscapeDataString(MessageBody(context, appVersion, windowsVersion)));

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
