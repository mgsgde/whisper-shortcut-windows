using System.Text.Json;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Mac MeetingSummaryMetaTests (PR #412, R208): a summary records the transcript it was built from,
/// and a later chunk — appended or spliced — makes it stale. No sidecar means "already current".
/// A missing summary is regenerated only when the sidecar says the request failed, at most three times.
/// </summary>
public class MeetingSummaryMetaTests
{
    private const string Transcript = "[00:00] Hello\n\n[01:00] World\n\n";

    private static IReadOnlyList<TranscriptChunk> Chunks(string text) => MeetingStore.ParseTranscript(text);

    [Fact]
    public void Meta_counts_chunks_and_records_the_last_start()
    {
        var meta = MeetingSummaryMeta.FromChunks(Chunks(Transcript));
        Assert.Equal(new MeetingSummaryMeta(2, 60), meta);
    }

    [Fact]
    public void A_summary_without_meta_counts_as_matching()
    {
        Assert.True(MeetingSummaryMeta.Matches(null, Chunks(Transcript)));
    }

    [Fact]
    public void An_unchanged_transcript_matches()
    {
        var chunks = Chunks(Transcript);
        var meta = MeetingSummaryMeta.FromChunks(chunks);
        Assert.True(MeetingSummaryMeta.Matches(meta, chunks));
    }

    [Fact]
    public void An_appended_chunk_makes_the_summary_stale()
    {
        var meta = MeetingSummaryMeta.FromChunks(Chunks(Transcript));
        Assert.False(MeetingSummaryMeta.Matches(meta, Chunks(Transcript + "[02:00] More\n\n")));
    }

    [Fact]
    public void A_chunk_spliced_in_before_the_last_one_makes_the_summary_stale()
    {
        var meta = MeetingSummaryMeta.FromChunks(Chunks(Transcript));
        var spliced = Chunks("[00:00] Hello\n\n[00:30] Late\n\n[01:00] World\n\n");
        Assert.False(MeetingSummaryMeta.Matches(meta, spliced));
    }

    [Fact]
    public void Meta_round_trips_as_json()
    {
        var meta = new MeetingSummaryMeta(3, 125);
        var json = JsonSerializer.Serialize(meta);
        Assert.Contains("\"chunkCount\":3", json.Replace(" ", ""));
        Assert.Contains("\"lastChunkStart\":125", json.Replace(" ", ""));
        Assert.DoesNotContain("failed", json);
        Assert.Equal(meta, JsonSerializer.Deserialize<MeetingSummaryMeta>(json));
        var failed = meta with { Failed = true, Attempts = 3 };
        var failedJson = JsonSerializer.Serialize(failed).Replace(" ", "");
        Assert.Contains("\"failed\":true", failedJson);
        Assert.Contains("\"attempts\":3", failedJson);
        Assert.Equal(failed, JsonSerializer.Deserialize<MeetingSummaryMeta>(JsonSerializer.Serialize(failed)));
    }

    [Fact]
    public void Meta_from_the_chunks_a_summary_was_built_from_flags_a_transcript_that_grew()
    {
        var source = Chunks(Transcript);
        var meta = MeetingSummaryMeta.FromChunks(source);
        Assert.Equal(MeetingSummaryMeta.FromChunks(Chunks(Transcript)), meta);
        var grown = Chunks(Transcript + "[02:00] Arrived during generation\n\n");
        Assert.False(MeetingSummaryMeta.Matches(meta, grown));
    }

    [Fact]
    public void An_old_meeting_without_a_sidecar_is_not_regenerated()
    {
        var chunks = Chunks(Transcript);
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, false, null, chunks, 0));
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Interrupted, false, null, chunks, 0));
        // summary.md deleted, successful sidecar left behind: that was not a failed request.
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(
            MeetingStatus.Ended, false, new MeetingSummaryMeta(2, 60), chunks, 0));
    }

    [Fact]
    public void A_failed_summary_is_retried_until_three_attempts()
    {
        var chunks = Chunks(Transcript);
        var failed = new MeetingSummaryMeta(2, 60, Failed: true, Attempts: 1);
        Assert.True(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, false, failed, chunks, 0));
        Assert.True(MeetingSummaryFreshness.NeedsNewSummary(
            MeetingStatus.Ended, false, failed with { Attempts = 2 }, chunks, 0));
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(
            MeetingStatus.Ended, false, failed with { Attempts = 3 }, chunks, 0));
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, false, failed, chunks, 1));
    }

    [Fact]
    public void Pending_audio_waits_until_the_retry()
    {
        var chunks = Chunks(Transcript);
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, false, null, chunks, 1));
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(
            MeetingStatus.Ended, true, new MeetingSummaryMeta(1, 0), chunks, 1));
    }

    [Fact]
    public void A_summary_without_meta_is_not_regenerated()
    {
        var chunks = Chunks(Transcript);
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, true, null, chunks, 0));
    }

    [Fact]
    public void A_stale_sidecar_needs_a_new_summary_and_a_current_one_does_not()
    {
        var chunks = Chunks(Transcript);
        var meta = MeetingSummaryMeta.FromChunks(chunks);
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, true, meta, chunks, 0));
        var grown = Chunks(Transcript + "[02:00] More\n\n");
        Assert.True(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Ended, true, meta, grown, 0));
    }

    [Fact]
    public void A_recording_meeting_and_an_empty_transcript_do_not()
    {
        var chunks = Chunks(Transcript);
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(MeetingStatus.Recording, false, null, chunks, 0));
        Assert.False(MeetingSummaryFreshness.NeedsNewSummary(
            MeetingStatus.Ended, false, null, Array.Empty<TranscriptChunk>(), 0));
    }
}

public class MeetingSummaryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-summary-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 14, 5, 9, TimeSpan.Zero);

    public MeetingSummaryStoreTests() => _store = new MeetingStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Save_writes_the_sidecar_and_a_later_splice_needs_a_new_summary()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        _store.AppendChunk(m.Id, 60, "World");
        _store.MarkEnded(m.Id, T0.AddMinutes(5));
        Assert.False(_store.NeedsNewSummary(m.Id));

        var meta = MeetingSummaryMeta.FromChunks(_store.ReadChunks(m.Id));
        _store.SaveSummary(m.Id, "## Punkte\n- eins", meta);
        Assert.Equal(meta, _store.ReadSummaryMeta(m.Id));
        Assert.False(_store.NeedsNewSummary(m.Id));

        _store.AppendChunk(m.Id, 30, "Late");
        Assert.True(_store.NeedsNewSummary(m.Id));
        // A stale sidecar is regenerated, and that retry is not a failed-summary attempt.
        Assert.True(_store.NoteSummaryRecoveryAttempt(m.Id));
        Assert.Equal(meta, _store.ReadSummaryMeta(m.Id));
        Assert.Equal(new[] { "Hello", "Late", "World" }, _store.ReadChunks(m.Id).Select(c => c.Text));
    }

    [Fact]
    public void Meta_from_before_generation_stays_stale_when_a_chunk_arrives_first()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        var source = MeetingSummaryMeta.FromChunks(_store.ReadChunks(m.Id));
        _store.AppendChunk(m.Id, 90, "Arrived during generation");
        _store.SaveSummary(m.Id, "## Kurz", source);
        Assert.Equal(source, _store.ReadSummaryMeta(m.Id));
        Assert.True(_store.NeedsNewSummary(m.Id));
    }

    [Fact]
    public void A_summary_saved_without_meta_matches_the_file_as_it_is()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        _store.SaveSummary(m.Id, "## Kurz");
        Assert.False(_store.NeedsNewSummary(m.Id));
    }

    [Fact]
    public void Pending_audio_a_recording_and_an_ended_meeting_without_a_failure_do_not_need_a_summary()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        Assert.False(_store.NeedsNewSummary(m.Id));
        _store.KeepPendingAudio(m.Id, 30, new byte[] { 1, 2, 3, 4 });
        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        Assert.False(_store.NeedsNewSummary(m.Id));
        _store.RemovePendingAudio(m.Id, 30);
        Assert.False(_store.NeedsNewSummary(m.Id));
    }

    [Fact]
    public void A_failed_sidecar_retries_until_three_attempts_and_a_meeting_without_one_does_not()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        Assert.False(_store.NeedsNewSummary(m.Id));

        _store.SaveSummaryFailure(m.Id, new MeetingSummaryMeta(1, 0, Failed: true, Attempts: 1));
        Assert.Null(_store.ReadSummary(m.Id));
        Assert.True(_store.NeedsNewSummary(m.Id));
        var sidecar = File.ReadAllText(Path.Combine(m.Folder, "summary.meta.json")).Replace(" ", "");
        Assert.Contains("\"failed\":true", sidecar);
        Assert.Contains("\"attempts\":1", sidecar);

        Assert.True(_store.NoteSummaryRecoveryAttempt(m.Id));
        Assert.Equal(2, _store.ReadSummaryMeta(m.Id)!.Attempts);
        Assert.True(_store.NoteSummaryRecoveryAttempt(m.Id));
        Assert.Equal(3, _store.ReadSummaryMeta(m.Id)!.Attempts);
        Assert.False(_store.NeedsNewSummary(m.Id));
        Assert.False(_store.NoteSummaryRecoveryAttempt(m.Id));

        _store.SaveSummaryFailure(m.Id, new MeetingSummaryMeta(1, 0, Failed: true, Attempts: 3));
        Assert.False(_store.NeedsNewSummary(m.Id));
    }

    [Fact]
    public void A_corrupt_sidecar_counts_as_no_meta()
    {
        var m = _store.Create(T0, "mic");
        _store.AppendChunk(m.Id, 0, "Hello");
        _store.MarkEnded(m.Id, T0.AddMinutes(1));
        _store.SaveSummary(m.Id, "## Kurz");
        File.WriteAllText(Path.Combine(m.Folder, "summary.meta.json"), "{not json");
        Assert.Null(_store.ReadSummaryMeta(m.Id));
        Assert.False(_store.NeedsNewSummary(m.Id));
    }
}
