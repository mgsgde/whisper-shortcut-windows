using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Live diktieren: who may use it, how pieces are joined at the cursor, what is held back, how the clipboard ends.</summary>
public class LiveDictationTests
{
    // ---- Availability ----

    [Fact]
    public void Available_when_nothing_is_missing()
    {
        Assert.Null(LiveDictation.UnavailableReason(duringMeeting: false, speechIsLocal: true, speechInstalled: true, autoPaste: true));
    }

    [Fact]
    public void Each_missing_condition_names_its_own_reason()
    {
        Assert.Equal(LiveUnavailable.DuringMeeting, LiveDictation.UnavailableReason(true, true, true, true));
        Assert.Equal(LiveUnavailable.NotOffline, LiveDictation.UnavailableReason(false, false, true, true));
        Assert.Equal(LiveUnavailable.NotDownloaded, LiveDictation.UnavailableReason(false, true, false, true));
        Assert.Equal(LiveUnavailable.AutoPasteOff, LiveDictation.UnavailableReason(false, true, true, false));
    }

    [Fact]
    public void Reasons_are_reported_in_the_order_the_user_fixes_them()
    {
        Assert.Equal(LiveUnavailable.DuringMeeting, LiveDictation.UnavailableReason(true, false, false, false));
        Assert.Equal(LiveUnavailable.NotOffline, LiveDictation.UnavailableReason(false, false, false, false));
        Assert.Equal(LiveUnavailable.NotDownloaded, LiveDictation.UnavailableReason(false, true, false, false));
    }

    [Fact]
    public void Every_reason_has_a_german_message()
    {
        foreach (var reason in Enum.GetValues<LiveUnavailable>())
            Assert.False(string.IsNullOrWhiteSpace(LiveDictation.Message(reason)));
        Assert.Equal("Live diktieren geht nicht, solange ein Live Meeting läuft.", LiveDictation.Message(LiveUnavailable.DuringMeeting));
    }

    // ---- Chunk policy ----

    [Fact]
    public void Chunk_policy_cuts_at_short_pauses()
    {
        var p = LiveDictation.ChunkPolicy;
        Assert.Equal(TimeSpan.FromSeconds(1.5), p.MinChunk);
        Assert.Equal(TimeSpan.FromSeconds(0.6), p.SilenceDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.2), p.MeterWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), p.MaxChunk);
        Assert.Equal(-40f, p.SilenceThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(0.4), LiveDictation.PieceGap);
        Assert.Equal(TimeSpan.FromMilliseconds(50), LiveDictation.PrePasteDelay);
    }

    private static short[] Tone(double seconds)
    {
        var s = new short[(int)(seconds * 16000)];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
        return s;
    }

    private static short[] Silence(double seconds) => new short[(int)(seconds * 16000)];

    [Fact]
    public void Chunker_cuts_after_a_pause_once_the_piece_is_long_enough()
    {
        var chunker = new MeetingChunker(16000, LiveDictation.ChunkPolicy);
        var chunks = new List<MeetingAudioChunk>();
        chunks.AddRange(chunker.Write(Tone(2.0)));
        Assert.Empty(chunks);
        chunks.AddRange(chunker.Write(Silence(0.7)));
        chunks.AddRange(chunker.Write(Tone(1.0)));
        var chunk = Assert.Single(chunks);
        Assert.False(chunk.Silent);
    }

    [Fact]
    public void Chunker_does_not_cut_a_piece_under_the_minimum()
    {
        var chunker = new MeetingChunker(16000, LiveDictation.ChunkPolicy);
        // 0.8 s + 0.7 s = 1.5 s with only 0.6 s of silence reached at 1.4 s: under the 1.5 s minimum.
        var chunks = new List<MeetingAudioChunk>();
        chunks.AddRange(chunker.Write(Tone(0.8)));
        chunks.AddRange(chunker.Write(Silence(0.7)));
        Assert.Empty(chunks);
    }

    // ---- Piece finishing ----

    [Fact]
    public void FinishPiece_applies_spoken_punctuation()
    {
        Assert.Equal("Befund:", LiveDictation.FinishPiece("Befund Doppelpunkt"));
    }

    // ---- Joiner ----

    [Fact]
    public void Pieces_are_joined_with_single_spaces_none_at_the_start()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Der Patient", j.Next("Der Patient"));
        Assert.Equal(" klagt über Schmerzen", j.Next("  klagt über Schmerzen "));
        Assert.Equal("Der Patient klagt über Schmerzen", j.Inserted);
    }

    [Fact]
    public void No_space_after_or_before_a_line_break_none_before_closing_punctuation()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Befund:\n", j.Next("Befund:\n"));
        Assert.Equal("Knie rechts", j.Next("Knie rechts"));
        Assert.Equal("\n\nTherapie", j.Next("\n\nTherapie"));
        Assert.Equal(", Kühlung", j.Next(", Kühlung"));
    }

    [Fact]
    public void A_period_at_a_breath_is_dropped_when_the_sentence_goes_on_in_lowercase()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Der Patient klagt über Schmerzen", j.Next("Der Patient klagt über Schmerzen."));
        Assert.Equal(" seit drei Tagen", j.Next("seit drei Tagen."));
        Assert.Equal(".", j.Finish());
        Assert.Equal("Der Patient klagt über Schmerzen seit drei Tagen.", j.Inserted);
    }

    [Fact]
    public void A_held_period_is_kept_when_the_next_piece_starts_a_new_sentence()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Keine Schwellung", j.Next("Keine Schwellung."));
        Assert.Equal(". Röntgen unauffällig", j.Next("Röntgen unauffällig"));
        Assert.Equal("", j.Finish());
        Assert.Equal("Keine Schwellung. Röntgen unauffällig", j.Inserted);
    }

    [Fact]
    public void Abbreviations_short_words_numbers_and_ellipses_keep_their_period_at_once()
    {
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("zum Beispiel z.B."));
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("bei Dr."));
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("ca."));
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("am 12."));
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("und dann..."));
        Assert.False(LiveChunkJoiner.HoldsFinalPeriod("Was nun?"));
        Assert.True(LiveChunkJoiner.HoldsFinalPeriod("über Schmerzen."));
        Assert.True(LiveChunkJoiner.HoldsFinalPeriod("Knie-Endoprothese."));
    }

    [Fact]
    public void Empty_pieces_insert_nothing_and_keep_a_held_period()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("", j.Next("   "));
        Assert.Equal("Schmerzen", j.Next("Schmerzen."));
        Assert.Equal("", j.Next(""));
        Assert.Equal(".", j.Finish());
    }

    // ---- Drift ----

    [Fact]
    public void Drift_counts_word_edits_ignoring_case_and_punctuation()
    {
        var same = LiveDictation.Drift("der patient klagt", "Der Patient klagt.");
        Assert.Equal(0, same.EditDistance);
        var changed = LiveDictation.Drift("Röntgen des rechten Knies", "Röntgen des rechten Kniegelenks in zwei Ebenen");
        Assert.Equal(4, changed.InsertedWords);
        Assert.Equal(7, changed.WholeFileWords);
        Assert.Equal(4, changed.EditDistance);
    }

    // ---- Session ----

    private sealed class Cursor
    {
        public List<string> Texts { get; } = new();
        public bool AppInFront { get; set; } = true;
        public string Document => string.Concat(Texts);
    }

    private static byte[] Wav(string name) => Encoding.UTF8.GetBytes(name);

    private static LiveDictationSession Session(Cursor cursor, Dictionary<string, string> texts,
        Dictionary<string, int>? delaysMs = null, HashSet<string>? failing = null)
    {
        return new LiveDictationSession(
            decode: async (wav, ct) =>
            {
                var name = Encoding.UTF8.GetString(wav);
                if (delaysMs != null && delaysMs.TryGetValue(name, out var delay)) await Task.Delay(delay, ct);
                if (failing != null && failing.Contains(name)) throw new InvalidDataException("bad audio");
                return texts.TryGetValue(name, out var t) ? t : "";
            },
            insert: text =>
            {
                if (!cursor.AppInFront) return Task.FromResult(false);
                cursor.Texts.Add(text);
                return Task.FromResult(true);
            });
    }

    [Fact]
    public async Task Pieces_reach_the_cursor_in_speaking_order_even_when_an_earlier_decode_is_slower()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["a"] = "Erstens", ["b"] = "zweitens" }, new() { ["a"] = 80 });
        s.Add(Wav("a"), false);
        s.Add(Wav("b"), false);
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal("Erstens zweitens", cursor.Document);
        Assert.Equal(new LiveDictationOutcome("Erstens zweitens", "", 2, 0), outcome);
    }

    [Fact]
    public async Task Text_is_held_while_another_window_is_in_front_and_goes_in_when_it_is_back()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["a"] = "Befund", ["b"] = "unauffällig" });
        cursor.AppInFront = false;
        s.Add(Wav("a"), false);
        s.Add(Wav("b"), false);
        await s.DrainAsync();
        Assert.Empty(cursor.Texts);
        Assert.True(s.HasHeldText);
        cursor.AppInFront = true;
        s.RetryHeld();
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal(new[] { "Befund unauffällig" }, cursor.Texts);
        Assert.Equal("", outcome.HeldText);
    }

    [Fact]
    public async Task Repeated_retries_queue_one_attempt_at_a_time()
    {
        var cursor = new Cursor { AppInFront = false };
        var attempts = 0;
        var gate = new TaskCompletionSource<bool>();
        var s = new LiveDictationSession(
            decode: (_, _) => Task.FromResult("Befund"),
            insert: async _ => { attempts++; return await gate.Task; });
        s.Add(Wav("a"), false); // first attempt waits on the gate
        for (int i = 0; i < 5; i++) s.RetryHeld(); // the app's timer while the window is in front
        gate.SetResult(false);
        await s.DrainAsync();
        Assert.Equal(2, attempts); // the piece's own attempt plus one queued retry, not five
        Assert.True(s.HasHeldText);
    }

    [Fact]
    public async Task Text_still_held_at_stop_is_handed_back_never_inserted_elsewhere()
    {
        var cursor = new Cursor { AppInFront = false };
        var s = Session(cursor, new() { ["a"] = "Diagnose Gonarthrose." });
        s.Add(Wav("a"), false);
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Empty(cursor.Texts);
        Assert.Equal("Diagnose Gonarthrose.", outcome.HeldText);
        Assert.Equal("Diagnose Gonarthrose.", outcome.Text);
    }

    [Fact]
    public async Task A_recording_that_never_rotated_is_decoded_from_the_delivered_file()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["merged"] = "Kurzer Satz." });
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal("Kurzer Satz.", cursor.Document);
        Assert.Equal(1, outcome.Pieces);
    }

    [Fact]
    public async Task Silent_pieces_are_skipped_and_the_delivered_file_is_not_decoded_on_top()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["a"] = "Hallo", ["merged"] = "Hallo doppelt" });
        s.Add(Wav("a"), false);
        s.Add(Wav("tail"), true);
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal("Hallo", cursor.Document);
        Assert.Equal(1, outcome.Pieces);
    }

    [Fact]
    public async Task A_failed_piece_is_counted_the_others_still_go_in()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["a"] = "eins", ["c"] = "drei" }, failing: new() { "b" });
        foreach (var name in new[] { "a", "b", "c" }) s.Add(Wav(name), false);
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal("eins drei", cursor.Document);
        Assert.Equal(1, outcome.FailedPieces);
        Assert.Equal(3, outcome.Pieces);
    }

    [Fact]
    public async Task After_cancel_nothing_more_is_inserted_and_finish_throws()
    {
        var cursor = new Cursor();
        var s = Session(cursor, new() { ["a"] = "eins", ["b"] = "zwei" }, new() { ["a"] = 80 });
        s.Add(Wav("a"), false);
        s.Add(Wav("b"), false);
        s.Cancel();
        Assert.True(s.IsCancelled);
        await Assert.ThrowsAsync<OperationCanceledException>(() => s.FinishAsync(Wav("merged")));
        Assert.Empty(cursor.Texts);
    }

    [Fact]
    public async Task An_insert_that_throws_holds_the_text_and_does_not_break_the_chain()
    {
        var calls = 0;
        var s = new LiveDictationSession(
            (wav, ct) => Task.FromResult(Encoding.UTF8.GetString(wav)),
            _ => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(true));
        s.Add(Wav("eins"), false);
        await s.DrainAsync();
        Assert.True(s.HasHeldText);
        s.RetryHeld();
        var outcome = await s.FinishAsync(Wav("merged"));
        Assert.Equal("", outcome.HeldText);
        Assert.Equal("eins", outcome.Text);
    }

    // ---- Clipboard at the end ----

    private static LiveDictationOutcome Outcome(string text = "", string held = "", int failed = 0) =>
        new(text, held, 1, failed);

    [Fact]
    public void Cancelled_restores_the_clipboard_when_restore_is_on()
    {
        Assert.Equal(LiveClipboardEnd.RestoreBefore, LiveDictation.ClipboardAtEnd(true, true, null, false));
    }

    [Fact]
    public void Cancelled_without_restore_clears_later_when_asked()
    {
        Assert.Equal(LiveClipboardEnd.KeepTextThenClear, LiveDictation.ClipboardAtEnd(false, true, null, false));
    }

    [Fact]
    public void Cancelled_without_restore_and_clear_leaves_the_clipboard()
    {
        Assert.Equal(LiveClipboardEnd.LeaveAsIs, LiveDictation.ClipboardAtEnd(false, false, null, false));
    }

    [Fact]
    public void Failed_pieces_with_a_fallback_keep_the_fallback()
    {
        Assert.Equal(LiveClipboardEnd.KeepFallback, LiveDictation.ClipboardAtEnd(true, false, Outcome("x", failed: 1), true));
    }

    [Fact]
    public void Failed_pieces_without_a_fallback_leave_the_clipboard()
    {
        Assert.Equal(LiveClipboardEnd.LeaveAsIs, LiveDictation.ClipboardAtEnd(true, false, Outcome("x", failed: 1), false));
    }

    [Fact]
    public void Held_text_goes_on_the_clipboard()
    {
        Assert.Equal(LiveClipboardEnd.KeepHeld, LiveDictation.ClipboardAtEnd(true, false, Outcome("x", "y"), false));
    }

    [Fact]
    public void Inserted_text_restores_the_clipboard_when_restore_is_on()
    {
        Assert.Equal(LiveClipboardEnd.RestoreBefore, LiveDictation.ClipboardAtEnd(true, true, Outcome("x"), false));
    }

    [Fact]
    public void Inserted_text_without_restore_stays_or_clears_later()
    {
        Assert.Equal(LiveClipboardEnd.KeepText, LiveDictation.ClipboardAtEnd(false, false, Outcome("x"), false));
        Assert.Equal(LiveClipboardEnd.KeepTextThenClear, LiveDictation.ClipboardAtEnd(false, true, Outcome("x"), false));
    }

    [Fact]
    public void Nothing_dictated_restores_the_clipboard()
    {
        Assert.Equal(LiveClipboardEnd.RestoreBefore, LiveDictation.ClipboardAtEnd(false, false, Outcome(), false));
    }
}
