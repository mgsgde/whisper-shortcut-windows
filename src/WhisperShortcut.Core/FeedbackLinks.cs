namespace WhisperShortcut.Core;

/// <summary>Where feedback can be sent.</summary>
public enum FeedbackChannel
{
    /// <summary>WhatsApp chat with the developer.</summary>
    WhatsApp,
    /// <summary>E-mail (mailto:).</summary>
    Email,
    /// <summary>A new GitHub issue.</summary>
    GitHub,
}

/// <summary>Links and pre-filled messages for "Feedback senden" (Mac FeedbackLinks and ChatView sendFeedbackFromChat).</summary>
public static class FeedbackLinks
{
    /// <summary>WhatsApp number of the developer, without "+".</summary>
    public const string WhatsAppNumber = "4917641952181";

    /// <summary>E-mail address for feedback.</summary>
    public const string SupportEmail = "mgsgde@gmail.com";

    /// <summary>The GitHub repository of the Windows app.</summary>
    public const string GitHubUrl = "https://github.com/mgsgde/whisper-shortcut-windows";

    /// <summary>Notice after WhatsApp was opened with the end of the chat attached.</summary>
    public const string OpenedWithChat = "WhatsApp geöffnet, mit dem Ende dieses Chats im Anhang. Vor dem Senden bearbeiten.";

    /// <summary>Notice after WhatsApp was opened without chat context.</summary>
    public const string OpenedEmpty = "WhatsApp geöffnet. Bearbeite die Nachricht vor dem Senden.";

    /// <summary>Notice when WhatsApp could not be opened.</summary>
    public const string OpenFailed = "WhatsApp ließ sich nicht öffnen – die anderen Kontaktwege findest du im Menü unter „Feedback senden“.";

    /// <summary>
    /// The pre-filled message: greeting, the optional <paramref name="context"/>, then app and Windows version.
    /// English on purpose: it goes to the developer.
    /// </summary>
    public static string MessageBody(string? context, string appVersion, string windowsVersion)
    {
        var text = "Hi! I have feedback about WhisperShortcut:\n\n";
        if (!string.IsNullOrWhiteSpace(context)) text += context.Trim() + "\n\n";
        return text + $"---\nApp version: {appVersion}\nWindows: {windowsVersion}";
    }

    /// <summary>The link that opens <paramref name="channel"/> with <see cref="MessageBody"/> filled in.</summary>
    public static Uri Url(FeedbackChannel channel, string? context, string appVersion, string windowsVersion)
    {
        var body = Uri.EscapeDataString(MessageBody(context, appVersion, windowsVersion));
        return channel switch
        {
            FeedbackChannel.WhatsApp => new Uri($"https://wa.me/{WhatsAppNumber}?text={body}"),
            FeedbackChannel.Email => new Uri(
                $"mailto:{SupportEmail}?subject={Uri.EscapeDataString($"WhisperShortcut feedback ({appVersion})")}&body={body}"),
            _ => new Uri($"{GitHubUrl}/issues/new?body={body}"),
        };
    }

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

    /// <summary>The excerpt with a lead-in line, or null when there is nothing to attach.</summary>
    public static string? ChatContext(IReadOnlyList<ChatMessage> messages)
    {
        var excerpt = ChatExcerpt(messages);
        return excerpt.Length == 0 ? null : "Here is the end of my chat for context:\n\n" + excerpt;
    }
}
