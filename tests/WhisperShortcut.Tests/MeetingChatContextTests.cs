using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MeetingChatContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-mcc-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 14, 5, 9, TimeSpan.Zero);

    public MeetingChatContextTests() => _store = new MeetingStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Live_has_the_intro_and_the_transcript_or_nothing()
    {
        Assert.Equal("", MeetingChatContext.Live(null));
        Assert.Equal("", MeetingChatContext.Live("  \n"));
        Assert.Equal(
            "This chat is attached to a meeting that is being transcribed live. Everything said so far is below — answer the user's questions from it, and quote timestamps like [12:34] when it helps them find the moment.\n\nFull transcript of the meeting so far:\n[00:00] Hallo",
            MeetingChatContext.Live("[00:00] Hallo"));
    }

    [Fact]
    public void Live_keeps_only_the_last_MaxChars()
    {
        var text = new string('a', 10) + new string('b', MeetingChatContext.MaxChars);
        var ctx = MeetingChatContext.Live(text);
        Assert.EndsWith("transcript of the meeting so far:\n" + new string('b', MeetingChatContext.MaxChars), ctx);
        Assert.DoesNotContain("a", ctx.Split("so far:\n")[1]);
    }

    [Fact]
    public void Ended_joins_summary_and_transcript()
    {
        Assert.Equal("", MeetingChatContext.Ended(" ", null));
        Assert.Equal("Use the following meeting context to answer the user's questions.\n\nMeeting summary:\nKurz\n\nMeeting transcript:\n[00:00] Hi",
            MeetingChatContext.Ended("Kurz", "[00:00] Hi"));
        Assert.Equal("Use the following meeting context to answer the user's questions.\n\nMeeting summary:\nKurz",
            MeetingChatContext.Ended("Kurz", ""));
        Assert.Equal("Use the following meeting context to answer the user's questions.\n\nMeeting transcript:\nT",
            MeetingChatContext.Ended(null, "T"));
    }

    [Fact]
    public void For_uses_live_context_while_recording_and_summary_after_the_end()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Guten Tag");
        Assert.StartsWith("This chat is attached to a meeting that is being transcribed live.", MeetingChatContext.For(_store, m.Id));
        Assert.Contains("[00:00] Guten Tag", MeetingChatContext.For(_store, m.Id));

        _store.MarkEnded(m.Id, T0.AddMinutes(5));
        _store.SaveSummary(m.Id, "Zusammenfassung");
        var ended = MeetingChatContext.For(_store, m.Id);
        Assert.StartsWith("Use the following meeting context", ended);
        Assert.Contains("Meeting summary:\nZusammenfassung", ended);
        Assert.Contains("Meeting transcript:\n", ended);
        Assert.Contains("[00:00] Guten Tag", ended);
    }

    [Fact]
    public void For_an_unknown_meeting_is_empty() => Assert.Equal("", MeetingChatContext.For(_store, "Meeting-gibt-es-nicht"));

    [Fact]
    public void Append_adds_a_separator_only_with_context()
    {
        Assert.Equal("S", MeetingChatContext.Append("S", ""));
        Assert.Equal("S\n\n---\n\nC", MeetingChatContext.Append("S", "C"));
    }

    [Fact]
    public void ChatSession_meeting_id_round_trips_and_defaults_to_null()
    {
        var chats = new ChatSessionStore(Path.Combine(_root, "chats"));
        var s = ChatSession.New(T0);
        s.Add(new ChatMessage(ChatRole.User, "Hi", T0));
        chats.Save(s);
        Assert.Null(chats.Load(s.Id)!.MeetingId);
        s.MeetingId = "Meeting-2026-11-02-150509";
        chats.Save(s);
        Assert.Equal("Meeting-2026-11-02-150509", chats.Load(s.Id)!.MeetingId);
        Assert.Contains("\"meetingId\"", File.ReadAllText(Path.Combine(_root, "chats", s.Id.ToString("D") + ".json")));
    }
}
