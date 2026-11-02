using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MeetingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-meetings-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingStore _store;

    public MeetingStoreTests() => _store = new MeetingStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 14, 5, 9, TimeSpan.Zero);

    [Fact]
    public void Create_makes_id_header_and_json_and_suffixes_duplicates()
    {
        var a = _store.Create(T0, "mic+system");
        var local = T0.ToLocalTime();
        Assert.Equal("Meeting-" + local.ToString("yyyy-MM-dd-HHmmss"), a.Id);
        Assert.Equal(MeetingStatus.Recording, a.Status);
        Assert.Null(a.EndedAt);
        Assert.Equal("Meeting " + local.ToString("dd.MM.yyyy HH:mm"), a.Title);
        Assert.Equal("# Meeting " + local.ToString("dd.MM.yyyy HH:mm") + "\n\n", _store.ReadTranscript(a.Id));

        var b = _store.Create(T0, "mic");
        var c = _store.Create(T0, "mic");
        Assert.Equal(a.Id + "-2", b.Id);
        Assert.Equal(a.Id + "-3", c.Id);

        var json = File.ReadAllText(Path.Combine(a.Folder, "meeting.json"));
        Assert.Contains("\"startedAt\"", json);
        Assert.Contains("\"endedAt\": null", json);
        Assert.Contains("\"status\": \"recording\"", json);
        Assert.Contains("\"audioMode\": \"mic+system\"", json);
        Assert.Contains("\"transcriptRevision\": 0", json);
    }

    [Fact]
    public void AppendChunk_in_order_appends_and_keeps_the_revision()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hallo zusammen");
        _store.AppendChunk(m.Id, 61.4, "Zweiter Teil");
        _store.AppendChunk(m.Id, 70, "   ");
        var chunks = _store.ReadChunks(m.Id);
        Assert.Equal(new[] { 0.0, 61.0 }, chunks.Select(c => c.StartSeconds));
        Assert.Equal(new[] { "Hallo zusammen", "Zweiter Teil" }, chunks.Select(c => c.Text));
        Assert.Equal("[01:01]", chunks[1].Timestamp);
        Assert.Equal(0, _store.Get(m.Id)!.TranscriptRevision);
        Assert.EndsWith("[01:01] Zweiter Teil\n\n", _store.ReadTranscript(m.Id));
    }

    [Fact]
    public void AppendChunk_out_of_order_splices_and_bumps_the_revision()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "eins");
        _store.AppendChunk(m.Id, 120, "drei");
        var before = _store.ReadSince(m.Id, 0)!;
        Assert.False(before.Restarted);

        _store.AppendChunk(m.Id, 60, "zwei");
        Assert.Equal(new[] { "eins", "zwei", "drei" }, _store.ReadChunks(m.Id).Select(c => c.Text));
        Assert.Equal(1, _store.Get(m.Id)!.TranscriptRevision);
        Assert.StartsWith("# Meeting", _store.ReadTranscript(m.Id));

        var after = _store.ReadSince(m.Id, before.NextOffset, before.Revision)!;
        Assert.True(after.Restarted);
        Assert.Equal(1, after.Revision);
        Assert.Contains("[01:00] zwei", after.Text);
        Assert.Contains("[00:00] eins", after.Text);

        // A same-second late chunk goes before the existing one.
        _store.AppendChunk(m.Id, 60, "zwei-b");
        var texts = _store.ReadChunks(m.Id).Select(c => c.Text).ToList();
        Assert.True(texts.IndexOf("zwei-b") < texts.IndexOf("zwei"));
        Assert.Equal(2, _store.Get(m.Id)!.TranscriptRevision);
    }

    [Fact]
    public void ReadSince_returns_only_new_text_and_handles_bad_offsets()
    {
        var m = _store.Create(T0, "mic");
        var first = _store.ReadSince(m.Id, 0)!;
        Assert.True(first.IsLive);
        Assert.StartsWith("# Meeting", first.Text);

        _store.AppendChunk(m.Id, 0, "erste Zeile");
        var r1 = _store.ReadSince(m.Id, first.NextOffset, first.Revision)!;
        Assert.Equal("[00:00] erste Zeile\n\n", r1.Text);
        Assert.False(r1.Restarted);

        _store.AppendChunk(m.Id, 10, "zweite Zeile");
        var r2 = _store.ReadSince(m.Id, r1.NextOffset, r1.Revision)!;
        Assert.Equal("[00:10] zweite Zeile\n\n", r2.Text);

        var r3 = _store.ReadSince(m.Id, r2.NextOffset, r2.Revision)!;
        Assert.Equal("", r3.Text);
        Assert.Equal(r2.NextOffset, r3.NextOffset);

        var bad = _store.ReadSince(m.Id, 99999, 0)!;
        Assert.True(bad.Restarted);
        Assert.Contains("erste Zeile", bad.Text);
        Assert.True(_store.ReadSince(m.Id, -1)!.Restarted);
        Assert.Null(_store.ReadSince("Meeting-nope", 0));

        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        Assert.False(_store.ReadSince(m.Id, 0)!.IsLive);
    }

    [Fact]
    public void ReadSince_never_splits_a_utf8_sequence_or_a_half_written_line()
    {
        var m = _store.Create(T0, "mic");
        var path = Path.Combine(m.Folder, "transcript.md");
        var start = _store.ReadSince(m.Id, 0)!;
        // Simulate a writer caught in the middle of "Grüße": the file ends inside the two-byte "ü".
        var full = Encoding.UTF8.GetBytes("[00:00] Grüße\n\n");
        var cut = Array.IndexOf(full, (byte)0xC3) + 1;
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(full, 0, cut);

        var partial = _store.ReadSince(m.Id, start.NextOffset)!;
        Assert.Equal("", partial.Text);
        Assert.Equal(start.NextOffset, partial.NextOffset);

        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(full, cut, full.Length - cut);
        var rest = _store.ReadSince(m.Id, partial.NextOffset)!;
        Assert.Equal("[00:00] Grüße\n\n", rest.Text);
        Assert.DoesNotContain('�', rest.Text);
    }

    [Fact]
    public void List_is_newest_first_and_Get_rejects_invalid_ids()
    {
        Assert.Empty(new MeetingStore(Path.Combine(_root, "missing")).List());
        var old = _store.Create(T0, "mic");
        var recent = _store.Create(T0.AddHours(2), "mic");
        Directory.CreateDirectory(Path.Combine(_root, "no-json-here"));
        Assert.Equal(new[] { recent.Id, old.Id }, _store.List().Select(x => x.Id));
        Assert.Equal(recent.Id, _store.Live()!.Id);
        _store.MarkEnded(recent.Id, T0.AddHours(3));
        Assert.Equal(old.Id, _store.Live()!.Id);

        Assert.Null(_store.Get(".."));
        Assert.Null(_store.Get("a/b"));
        Assert.Null(_store.Get("a\\b"));
        Assert.Null(_store.Get(""));
        Assert.Null(_store.Get("Meeting-unknown"));
        Assert.Null(_store.ReadTranscript("Meeting-unknown"));
        Assert.Throws<ArgumentException>(() => _store.FolderOf(".."));
    }

    [Fact]
    public void Title_duration_and_summary_are_stored()
    {
        var m = _store.Create(T0, "mic", "Sprint Review");
        Assert.Equal("Sprint Review", m.Title);
        _store.SetTitle(m.Id, "  Planung  ");
        _store.SetDuration(m.Id, 123.5);
        Assert.False(_store.Get(m.Id)!.HasSummary);
        _store.SaveSummary(m.Id, "## Punkte\n- eins");
        var info = _store.Get(m.Id)!;
        Assert.Equal("Planung", info.Title);
        Assert.Equal(123.5, info.DurationSeconds);
        Assert.True(info.HasSummary);
        Assert.Equal("## Punkte\n- eins", _store.ReadSummary(m.Id));
        _store.MarkEnded(m.Id, T0.AddMinutes(5));
        Assert.Equal(MeetingStatus.Ended, _store.Get(m.Id)!.Status);
        Assert.Equal(T0.AddMinutes(5), _store.Get(m.Id)!.EndedAt);
    }

    [Fact]
    public void Search_matches_all_words_case_insensitively_in_chunks_title_and_summary()
    {
        var a = _store.Create(T0, "mic", "Budget Runde");
        _store.AppendChunk(a.Id, 0, "Wir sprechen über das Budget für das nächste Jahr.");
        _store.AppendChunk(a.Id, 30, "Das Jahr war gut.");
        var b = _store.Create(T0.AddHours(1), "mic", "Anderes");
        _store.AppendChunk(b.Id, 5, "BUDGET und Jahr zusammen");
        _store.SaveSummary(b.Id, "## Fazit\n- Das Budget bleibt gleich");

        var hits = _store.Search("budget JAHR");
        Assert.Equal(2, hits.Count(h => h.StartSeconds != null));
        Assert.Equal(b.Id, hits[0].MeetingId); // newest meeting first
        Assert.Equal(new[] { 0.0 }, hits.Where(h => h.MeetingId == a.Id && h.StartSeconds != null).Select(h => h.StartSeconds!.Value));

        var titleHit = _store.Search("runde");
        Assert.Single(titleHit);
        Assert.Null(titleHit[0].StartSeconds);
        Assert.Equal(a.Id, titleHit[0].MeetingId);

        var summaryHit = _store.Search("fazit");
        Assert.Single(summaryHit);
        Assert.Null(summaryHit[0].StartSeconds);
        Assert.Equal(b.Id, summaryHit[0].MeetingId);

        Assert.Empty(_store.Search("   "));
        Assert.Empty(_store.Search("gibtesnicht"));
        Assert.Single(_store.Search("budget", maxResults: 1));
    }

    [Fact]
    public void Search_snippet_is_short_flat_and_marks_cuts()
    {
        var m = _store.Create(T0, "mic");
        var text = string.Join(" ", Enumerable.Repeat("blabla", 60)) + "\n\nNADEL " + string.Join("  ", Enumerable.Repeat("danach", 60));
        _store.AppendChunk(m.Id, 0, text);
        var hit = Assert.Single(_store.Search("nadel"));
        Assert.True(hit.Snippet.Length <= 162);
        Assert.Contains("NADEL", hit.Snippet);
        Assert.StartsWith("…", hit.Snippet);
        Assert.EndsWith("…", hit.Snippet);
        Assert.DoesNotContain('\n', hit.Snippet);
        Assert.DoesNotContain("  ", hit.Snippet);
    }

    [Fact]
    public void ParseTranscript_handles_big_minutes_and_markers_inside_a_line()
    {
        var text = "# Meeting 02.11.2026 14:05\n\n[75:03] Spät im Meeting\n\n[75:40] Wir sehen uns um [1:23] wieder\nzweite Zeile\n\n[76:00]   \n\n[77:01] Ende\n";
        var chunks = MeetingStore.ParseTranscript(text);
        Assert.Equal(new[] { 75 * 60 + 3.0, 75 * 60 + 40.0, 77 * 60 + 1.0 }, chunks.Select(c => c.StartSeconds));
        Assert.Equal("Wir sehen uns um [1:23] wieder\nzweite Zeile", chunks[1].Text);
        Assert.Equal("[75:03]", chunks[0].Timestamp);
        Assert.Empty(MeetingStore.ParseTranscript("# nur Kopf\n\n"));
        Assert.Equal("[00:00]", MeetingStore.FormatTimestamp(0.9));
        Assert.Equal("[75:03]", MeetingStore.FormatTimestamp(4503.7));
    }

    [Fact]
    public void RecoverInterrupted_marks_meetings_and_converts_partials()
    {
        var dead = _store.Create(T0, "mic");
        var alive = _store.Create(T0.AddHours(1), "mic");
        var done = _store.Create(T0.AddHours(2), "mic");
        _store.MarkEnded(done.Id, T0.AddHours(3));

        var pcm = Synth.Speech(1.0);
        using (var fs = _store.OpenPartialAudio(dead.Id, 60))
        {
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            fs.Write(bytes, 0, bytes.Length);
        }
        using (_store.OpenPartialAudio(dead.Id, 120)) { } // empty partial: deleted

        var changed = _store.RecoverInterrupted(alive.Id);
        Assert.Equal(new[] { dead.Id }, changed.Select(c => c.Id));
        var info = _store.Get(dead.Id)!;
        Assert.Equal(MeetingStatus.Interrupted, info.Status);
        Assert.NotNull(info.EndedAt);
        Assert.Equal(MeetingStatus.Recording, _store.Get(alive.Id)!.Status);
        Assert.Equal(MeetingStatus.Ended, _store.Get(done.Id)!.Status);

        var pending = _store.ListPendingAudio(dead.Id);
        var only = Assert.Single(pending);
        Assert.Equal(60.0, only.StartSeconds);
        var samples = Wav.ReadMonoFloat(File.ReadAllBytes(only.Path), out var rate);
        Assert.Equal(16000, rate);
        Assert.Equal(16000, samples.Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(dead.Folder, "audio-pending"), "*.pcm"));
        Assert.Equal(1, info.PendingAudioChunks);
        Assert.Empty(_store.RecoverInterrupted(alive.Id)); // nothing left to do
    }

    [Fact]
    public void Pending_audio_round_trip_and_file_names()
    {
        var a = _store.Create(T0, "mic");
        var b = _store.Create(T0.AddHours(1), "mic");
        Assert.Empty(_store.IdsWithPendingAudio());
        var wav = Wav.Encode16(Synth.Speech(0.5), 16000);
        _store.KeepPendingAudio(b.Id, 120, wav);
        _store.KeepPendingAudio(b.Id, 60.25, wav);
        _store.KeepPendingAudio(a.Id, 0, wav);

        Assert.Equal(new[] { 60.25, 120.0 }, _store.ListPendingAudio(b.Id).Select(p => p.StartSeconds));
        Assert.Equal(wav, File.ReadAllBytes(_store.ListPendingAudio(a.Id)[0].Path));
        Assert.Equal(new[] { a.Id, b.Id }, _store.IdsWithPendingAudio());
        Assert.Equal(2, _store.Get(b.Id)!.PendingAudioChunks);

        _store.RemovePendingAudio(a.Id, 0);
        Assert.False(Directory.Exists(Path.Combine(a.Folder, "audio-pending")));
        _store.RemovePendingAudio(b.Id, 60.25);
        Assert.Single(_store.ListPendingAudio(b.Id));
        Assert.Equal(new[] { b.Id }, _store.IdsWithPendingAudio());

        Assert.Equal("chunk-0000060000.wav", MeetingStore.PendingFileName(60));
        Assert.Equal(60.0, MeetingStore.StartSecondsFromFileName("chunk-0000060000.wav"));
        Assert.Equal(60.25, MeetingStore.StartSecondsFromFileName(MeetingStore.PendingFileName(60.25)));
        Assert.Null(MeetingStore.StartSecondsFromFileName("chunk-0000060000.pcm"));
        Assert.Null(MeetingStore.StartSecondsFromFileName("chunk-60.wav"));
        Assert.Null(MeetingStore.StartSecondsFromFileName("other.wav"));
    }

    [Fact]
    public void Partial_audio_appends_and_is_deleted()
    {
        var m = _store.Create(T0, "mic");
        using (var fs = _store.OpenPartialAudio(m.Id, 0)) fs.Write(new byte[] { 1, 2 });
        using (var fs = _store.OpenPartialAudio(m.Id, 0)) fs.Write(new byte[] { 3, 4 });
        var file = Path.Combine(m.Folder, "audio-pending", "chunk-0000000000.pcm");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        Assert.Empty(_store.ListPendingAudio(m.Id)); // partials are not listed
        _store.DeletePartialAudio(m.Id, 0);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(m.Folder, "audio-pending")));
    }
}
