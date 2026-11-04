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
        Assert.Null(LiveDictation.UnavailableReason(duringMeeting: false, speechIsLocal: true, speechInstalled: true));
    }

    [Fact]
    public void Each_missing_condition_names_its_own_reason()
    {
        Assert.Equal(LiveUnavailable.DuringMeeting, LiveDictation.UnavailableReason(true, true, true));
        Assert.Equal(LiveUnavailable.NotParakeet, LiveDictation.UnavailableReason(false, false, true));
        Assert.Equal(LiveUnavailable.NotDownloaded, LiveDictation.UnavailableReason(false, true, false));
    }

    [Fact]
    public void Reasons_are_reported_in_the_order_the_user_fixes_them()
    {
        Assert.Equal(LiveUnavailable.DuringMeeting, LiveDictation.UnavailableReason(true, false, false));
        Assert.Equal(LiveUnavailable.NotParakeet, LiveDictation.UnavailableReason(false, false, false));
        Assert.Equal(LiveUnavailable.NotDownloaded, LiveDictation.UnavailableReason(false, true, false));
    }

    [Fact]
    public void Every_reason_has_a_german_message()
    {
        foreach (var reason in Enum.GetValues<LiveUnavailable>())
            Assert.False(string.IsNullOrWhiteSpace(LiveDictation.Message(reason)));
        Assert.Equal("Live diktieren ist während eines Meetings nicht verfügbar.", LiveDictation.Message(LiveUnavailable.DuringMeeting));
        Assert.Equal("Live diktieren funktioniert mit dem Offline-Modell Parakeet. Wähle es unter Einstellungen → Diktieren.",
            LiveDictation.Message(LiveUnavailable.NotParakeet));
        Assert.Equal("Lade Parakeet unter Einstellungen → Diktieren, um Live diktieren zu nutzen.",
            LiveDictation.Message(LiveUnavailable.NotDownloaded));
    }

    // ---- Dictate mode ----

    [Fact]
    public void Start_decision_dictate_never_depends_on_availability()
    {
        foreach (LiveUnavailable? reason in new LiveUnavailable?[] { null, LiveUnavailable.DuringMeeting, LiveUnavailable.NotParakeet, LiveUnavailable.NotDownloaded })
            Assert.Equal((LiveStartDecision.StartDictate, (LiveUnavailable?)null), LiveDictation.StartDecision(DictateMode.Dictate, reason));
    }

    [Fact]
    public void Start_decision_live_starts_when_available_and_refuses_with_the_reason_otherwise()
    {
        Assert.Equal((LiveStartDecision.StartLive, (LiveUnavailable?)null), LiveDictation.StartDecision(DictateMode.Live, null));
        foreach (var reason in new[] { LiveUnavailable.NotParakeet, LiveUnavailable.NotDownloaded })
            Assert.Equal((LiveStartDecision.Refuse, (LiveUnavailable?)reason), LiveDictation.StartDecision(DictateMode.Live, reason));
    }

    [Fact]
    public void Start_decision_live_during_a_meeting_starts_a_dictate_segment_instead_of_refusing() =>
        Assert.Equal((LiveStartDecision.StartDictate, (LiveUnavailable?)null),
            LiveDictation.StartDecision(DictateMode.Live, LiveUnavailable.DuringMeeting));

    // ---- Caret ----

    [Fact]
    public void Caret_log_kind_never_names_the_character()
    {
        Assert.Equal("unknown", CaretContext.Unknown.LogKind);
        Assert.Equal("start", CaretContext.StartOfText.LogKind);
        Assert.Equal("letter", CaretContext.Of('s').LogKind);
        Assert.Equal("space", CaretContext.Of(' ').LogKind);
        Assert.Equal("newline", CaretContext.Of('\n').LogKind);
        Assert.Equal("newline", CaretContext.Of('\r').LogKind);
        Assert.Equal("other", CaretContext.Of('.').LogKind);
        Assert.Equal(CaretContext.Unknown, default(CaretContext));
    }

    [Fact]
    public void Caret_from_text_reads_the_character_before_the_caret()
    {
        Assert.Equal(CaretContext.Unknown, LiveDictation.CaretFromText("abc", -1, false));
        Assert.Equal(CaretContext.Unknown, LiveDictation.CaretFromText("abc", 4, false));
        Assert.Equal(CaretContext.StartOfText, LiveDictation.CaretFromText("abc", 0, false));
        Assert.Equal(CaretContext.StartOfText, LiveDictation.CaretFromText("", 0, false));
        Assert.Equal(CaretContext.Of('a'), LiveDictation.CaretFromText("abc", 1, false));
        Assert.Equal(CaretContext.Of('c'), LiveDictation.CaretFromText("abc", 3, false));
    }

    [Fact]
    public void Caret_from_text_without_rich_edit_offsets_sees_a_lone_cr()
    {
        // Plain index: the '\r' of a "\r\n" pair is what sits before caret 1.
        Assert.Equal(CaretContext.Of('\r'), LiveDictation.CaretFromText("a\r\nb", 2, false));
        Assert.Equal(CaretContext.Of('\n'), LiveDictation.CaretFromText("a\r\nb", 3, false));
    }

    [Fact]
    public void Caret_from_text_counts_a_crlf_pair_as_one_position_for_rich_edit()
    {
        // Logical positions: a=1, "\r\n"=2, b=3.
        Assert.Equal(CaretContext.Of('a'), LiveDictation.CaretFromText("a\r\nb", 1, true));
        Assert.Equal(CaretContext.Of('\n'), LiveDictation.CaretFromText("a\r\nb", 2, true));
        Assert.Equal(CaretContext.Of('b'), LiveDictation.CaretFromText("a\r\nb", 3, true));
        Assert.Equal(CaretContext.Unknown, LiveDictation.CaretFromText("a\r\nb", 4, true));
        Assert.Equal(CaretContext.StartOfText, LiveDictation.CaretFromText("a\r\nb", 0, true));
        // A lone CR stays one position.
        Assert.Equal(CaretContext.Of('\r'), LiveDictation.CaretFromText("a\rb", 2, true));
    }

    // ---- Last insertion memory ----

    private static readonly DateTime MemoryNow = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static LastInsertion Memory(char character = 's', string app = "winword", double ageSeconds = 5) =>
        new(character, app, MemoryNow.AddSeconds(-ageSeconds));

    [Fact]
    public void Memory_seeds_an_unknown_caret_for_the_same_app()
    {
        var seeded = LastInsertion.Seed(CaretContext.Unknown, Memory(), "WINWORD", MemoryNow);
        Assert.Equal(CaretContext.Of('s'), seeded);
    }

    [Fact]
    public void Memory_is_ignored_for_another_app_when_expired_or_missing()
    {
        Assert.Equal(CaretContext.Unknown, LastInsertion.Seed(CaretContext.Unknown, Memory(), "notepad", MemoryNow));
        Assert.Equal(CaretContext.Unknown, LastInsertion.Seed(CaretContext.Unknown, Memory(ageSeconds: 601), "winword", MemoryNow));
        Assert.Equal(CaretContext.Unknown, LastInsertion.Seed(CaretContext.Unknown, null, "winword", MemoryNow));
        Assert.Equal(CaretContext.Unknown, LastInsertion.Seed(CaretContext.Unknown, Memory(), null, MemoryNow));
        // A clock that went backwards is not trusted either.
        Assert.Equal(CaretContext.Unknown, LastInsertion.Seed(CaretContext.Unknown, Memory(ageSeconds: -5), "winword", MemoryNow));
    }

    [Fact]
    public void A_readable_caret_wins_over_the_memory()
    {
        Assert.Equal(CaretContext.StartOfText, LastInsertion.Seed(CaretContext.StartOfText, Memory(), "winword", MemoryNow));
        Assert.Equal(CaretContext.Of('.'), LastInsertion.Seed(CaretContext.Of('.'), Memory(), "winword", MemoryNow));
    }

    [Fact]
    public void Make_keeps_the_last_character_nothing_for_empty_text_or_unknown_target()
    {
        Assert.Equal('s', LastInsertion.Make("… Dosis", "winword", MemoryNow)?.Character);
        Assert.Null(LastInsertion.Make("", "winword", MemoryNow));
        Assert.Null(LastInsertion.Make("a", null, MemoryNow));
        Assert.Null(LastInsertion.Make("a", "", MemoryNow));
    }

    [Fact]
    public void Seeded_with_the_previous_dictations_ending_the_next_piece_is_spaced_and_cased()
    {
        var afterWord = new LiveChunkJoiner();
        afterWord.Seed(CaretContext.Of('s'));
        Assert.Equal(" Weiter geht's", afterWord.Next("Weiter geht's"));

        var afterPeriod = new LiveChunkJoiner { HoldsPeriods = false };
        afterPeriod.Seed(CaretContext.Of('.'));
        Assert.Equal(" Weiter geht's", afterPeriod.Next("Weiter geht's"));

        var spokenOnly = new LiveChunkJoiner { HoldsPeriods = false };
        spokenOnly.Seed(CaretContext.Of('s'));
        Assert.Equal(" und dann", spokenOnly.Next("Und dann"));
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
        Assert.Equal(-45f, p.SilenceThresholdDb);
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

    private const PunctuationMode Auto = PunctuationMode.Automatic;
    private const PunctuationMode Spoken = PunctuationMode.SpokenOnly;

    [Fact]
    public void FinishPiece_applies_spoken_punctuation()
    {
        Assert.Equal("Befund:", LiveDictation.FinishPiece("Befund Doppelpunkt", Auto));
    }

    [Fact]
    public void Spoken_only_a_spoken_Punkt_ends_the_piece_with_a_period() =>
        Assert.EndsWith(".", LiveDictation.FinishPiece("befund Punkt", Spoken));

    [Fact]
    public void Spoken_only_keeps_the_model_period_strips_its_comma_automatic_keeps_both()
    {
        Assert.Equal("Der Befund ist unauffällig.", LiveDictation.FinishPiece("Der Befund ist unauffällig.", Spoken));
        Assert.Equal("Gangbild unauffällig.", LiveDictation.FinishPiece("Gangbild, unauffällig.", Spoken));
        Assert.Equal("Der Befund ist unauffällig.", LiveDictation.FinishPiece("Der Befund ist unauffällig.", Auto));
    }

    [Fact]
    public void Spoken_only_the_joiner_holds_a_spoken_final_period_like_a_model_one()
    {
        var joiner = new LiveChunkJoiner();
        Assert.StartsWith("befund", joiner.Next(LiveDictation.FinishPiece("befund Punkt", Spoken)));
        Assert.Equal(". Keine Auffälligkeiten", joiner.Next(LiveDictation.FinishPiece("Keine Auffälligkeiten", Spoken)));
    }

    [Fact]
    public void Standalone_fillers_are_dropped_real_words_stay()
    {
        Assert.Equal("", LiveDictation.FinishPiece("Mm-hmm", Auto));
        Assert.Equal("was ist das", LiveDictation.FinishPiece("Hmm, was ist das", Auto));
        Assert.Equal("", LiveDictation.FinishPiece("Hmmm", Auto));
        Assert.Equal("Basistherapie.", LiveDictation.FinishPiece("Basistherapie ähm Punkt", Spoken));
        Assert.Equal("Um zehn Uhr", LiveDictation.FinishPiece("Um zehn Uhr", Auto));
        Assert.Equal("Ah stimmt, aha und achso oh", LiveDictation.FinishPiece("Ah stimmt, aha und achso oh", Auto));
    }

    [Fact]
    public void Timed_marks_keep_the_spoken_comma_without_timings_the_old_rule_strips_it()
    {
        Assert.Equal("Gangbild, geht weiter.", LiveDictation.FinishPiece("Gangbild, geht weiter.", Spoken, timedMarks: true));
        Assert.Equal("Gangbild geht weiter.", LiveDictation.FinishPiece("Gangbild, geht weiter.", Spoken));
    }

    [Fact]
    public void A_spoken_exclamation_mark_stays_an_exclamation_mark_through_finish_piece()
    {
        Assert.Equal("Wirklich!", LiveDictation.FinishPiece("Wirklich!", Spoken, timedMarks: true));
        Assert.Equal("Wirklich.!", LiveDictation.FinishPiece("Wirklich.!", Spoken, timedMarks: true));
        Assert.Equal("Wirklich.", LiveDictation.FinishPiece("Wirklich.!", Spoken));
        // The word itself, with the periods Parakeet put around it, becomes "!" and not ".".
        Assert.Equal("Bitte Befund nachreichen!", LiveDictation.FinishPiece("Bitte Befund nachreichen. Ausrufezeichen.", Spoken));
        Assert.Equal("Bitte Befund nachreichen!", LiveDictation.FinishPiece("Bitte Befund nachreichen. Ausrufezeichen.", Spoken, timedMarks: true));
    }

    [Fact]
    public void Spoken_Semikolon_and_Strichpunkt_become_a_semicolon_with_and_without_timings()
    {
        foreach (var word in new[] { "Semikolon", "Strichpunkt" })
        {
            var sentence = $"Hüfte frei beweglich {word} Knie mit Erguss";
            Assert.Equal("Hüfte frei beweglich; Knie mit Erguss", LiveDictation.FinishPiece(sentence, Spoken));
            Assert.Equal("Hüfte frei beweglich; Knie mit Erguss", LiveDictation.FinishPiece(sentence, Spoken, timedMarks: true));
        }
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

    [Fact]
    public void Spoken_only_a_spoken_period_is_never_held_even_before_a_lowercase_word()
    {
        var j = new LiveChunkJoiner { HoldsPeriods = false };
        Assert.Equal("klagt.", j.Next("klagt."));
        Assert.Equal(" seit gestern", j.Next("seit gestern"));
        Assert.Equal("klagt. seit gestern", j.Inserted);
    }

    [Fact]
    public void Spoken_only_a_function_word_at_the_next_piece_start_is_lowercased_mid_sentence()
    {
        var j = new LiveChunkJoiner { HoldsPeriods = false };
        Assert.Equal("Sie klagt", j.Next("Sie klagt"));
        Assert.Equal(" über Schmerzen", j.Next("Über Schmerzen"));
        Assert.Equal("Sie klagt über Schmerzen", j.Inserted);
    }

    [Fact]
    public void Spoken_only_a_kept_model_period_lets_the_next_piece_keep_its_capital()
    {
        var j = new LiveChunkJoiner { HoldsPeriods = false };
        Assert.Equal("Die Patientin klagt.", j.Next(LiveDictation.FinishPiece("Die Patientin klagt.", Spoken)));
        Assert.Equal(" Über Schmerzen", j.Next(LiveDictation.FinishPiece("Über Schmerzen", Spoken)));
        Assert.Equal("Die Patientin klagt. Über Schmerzen", j.Inserted);
    }

    [Fact]
    public void Spoken_only_a_capital_stays_when_the_previous_piece_ended_the_sentence()
    {
        var j = new LiveChunkJoiner { HoldsPeriods = false };
        Assert.Equal("Ende.", j.Next("Ende."));
        Assert.Equal(" Über Schmerzen", j.Next("Über Schmerzen"));
        Assert.Equal("Ende. Über Schmerzen", j.Inserted);
    }

    [Fact]
    public void With_periods_held_a_capital_at_a_piece_start_stays()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Sie klagt", j.Next("Sie klagt"));
        Assert.Equal(" Über Schmerzen", j.Next("Über Schmerzen"));
        Assert.Equal("Sie klagt Über Schmerzen", j.Inserted);
    }

    [Fact]
    public void A_seeded_caret_spaces_the_first_piece_and_is_not_part_of_the_inserted_text()
    {
        var afterLetter = new LiveChunkJoiner();
        afterLetter.Seed(CaretContext.Of('e'));
        Assert.Equal(" Ah stimmt", afterLetter.Next("Ah stimmt"));
        Assert.Equal(" Ah stimmt", afterLetter.Inserted);

        var afterSpace = new LiveChunkJoiner();
        afterSpace.Seed(CaretContext.Of(' '));
        Assert.Equal("Ah stimmt", afterSpace.Next("Ah stimmt"));

        var afterNewline = new LiveChunkJoiner();
        afterNewline.Seed(CaretContext.Of('\n'));
        Assert.Equal("Ah stimmt", afterNewline.Next("Ah stimmt"));

        var afterCr = new LiveChunkJoiner();
        afterCr.Seed(CaretContext.Of('\r'));
        Assert.Equal("Ah stimmt", afterCr.Next("Ah stimmt"));

        var atStart = new LiveChunkJoiner();
        atStart.Seed(CaretContext.StartOfText);
        Assert.Equal("Ah stimmt", atStart.Next("Ah stimmt"));

        var unknown = new LiveChunkJoiner();
        unknown.Seed(CaretContext.Unknown);
        var today = new LiveChunkJoiner();
        Assert.Equal(today.Next("Der Patient"), unknown.Next("Der Patient"));
        Assert.Equal(today.Next("klagt über Schmerzen"), unknown.Next("klagt über Schmerzen"));
        Assert.Equal(today.Inserted, unknown.Inserted);
    }

    [Fact]
    public void Spoken_only_a_seeded_letter_lowercases_a_leading_function_word()
    {
        var j = new LiveChunkJoiner { HoldsPeriods = false };
        j.Seed(CaretContext.Of('g'));
        Assert.Equal(" und weiter", j.Next("Und weiter"));
        Assert.Equal(" und weiter", j.Inserted);
    }

    [Fact]
    public void A_spoken_paragraph_break_at_the_very_end_of_a_piece_is_not_lost()
    {
        var j = new LiveChunkJoiner();
        var first = LiveDictation.FinishPiece("Befund neuer Absatz", Auto);
        var second = LiveDictation.FinishPiece("Inspektion unauffällig", Auto);
        Assert.Equal("Befund\n\n", j.Next(first));
        Assert.Equal("Inspektion unauffällig", j.Next(second));
        Assert.Equal("Befund\n\nInspektion unauffällig", j.Inserted);
    }

    [Fact]
    public void A_slash_said_across_two_pieces_gets_no_space_on_either_side()
    {
        var afterSlash = new LiveChunkJoiner();
        Assert.Equal("L4/", afterSlash.Next("L4/"));
        Assert.Equal("L5", afterSlash.Next("L5"));
        Assert.Equal("L4/L5", afterSlash.Inserted);

        var beforeSlash = new LiveChunkJoiner();
        Assert.Equal("L4", beforeSlash.Next("L4"));
        Assert.Equal("/L5", beforeSlash.Next("/L5"));
        Assert.Equal("L4/L5", beforeSlash.Inserted);
    }

    [Fact]
    public void An_ellipsis_attaches_to_the_previous_piece()
    {
        var j = new LiveChunkJoiner();
        Assert.Equal("Gesamt", j.Next("Gesamt"));
        Assert.Equal("… und", j.Next("… und"));
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
