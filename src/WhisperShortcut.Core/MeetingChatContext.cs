namespace WhisperShortcut.Core;

/// <summary>The meeting text a chat attached to a meeting gets as extra system context (Mac MeetingListService).</summary>
public static class MeetingChatContext
{
    /// <summary>Most characters of the transcript that go into the context (Mac meetingContextMaxChars).</summary>
    public const int MaxChars = 60_000;

    /// <summary>Context of a meeting that is still recording: the transcript so far. "" when it is blank.</summary>
    public static string Live(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";
        return "This chat is attached to a meeting that is being transcribed live. Everything said so far is below — "
            + "answer the user's questions from it, and quote timestamps like [12:34] when it helps them find the moment.\n\n"
            + "Full transcript of the meeting so far:\n" + Tail(transcript);
    }

    /// <summary>Context of a finished meeting: its summary and transcript. "" when both are blank.</summary>
    public static string Ended(string? summary, string? transcript)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary)) parts.Add("Meeting summary:\n" + summary);
        if (!string.IsNullOrWhiteSpace(transcript)) parts.Add("Meeting transcript:\n" + Tail(transcript));
        return parts.Count == 0 ? "" : "Use the following meeting context to answer the user's questions.\n\n" + string.Join("\n\n", parts);
    }

    /// <summary>The context for <paramref name="meetingId"/> read from <paramref name="store"/>; "" for an unknown meeting.</summary>
    public static string For(MeetingStore store, string meetingId)
    {
        var info = store.Get(meetingId);
        if (info == null) return "";
        return info.Status == MeetingStatus.Recording
            ? Live(store.ReadTranscript(meetingId))
            : Ended(store.ReadSummary(meetingId), store.ReadTranscript(meetingId));
    }

    /// <summary><paramref name="systemPrompt"/> plus the context after a separator, or just the prompt when the context is empty.</summary>
    public static string Append(string systemPrompt, string context) =>
        context.Length == 0 ? systemPrompt : systemPrompt + "\n\n---\n\n" + context;

    // The newest MaxChars (the end of a long meeting matters most); never starts in the middle of a surrogate pair.
    private static string Tail(string text)
    {
        if (text.Length <= MaxChars) return text;
        var start = text.Length - MaxChars;
        if (char.IsLowSurrogate(text[start])) start++;
        return text[start..];
    }
}
