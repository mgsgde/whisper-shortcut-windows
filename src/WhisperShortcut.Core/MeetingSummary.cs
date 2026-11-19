using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

/// <summary>
/// Which transcript a summary was built from: how many chunks it had and where the last one starts.
/// Written next to summary.md as summary.meta.json. A later splice (recovered backlog, a chunk that
/// arrived while the model was still writing) no longer matches, so the summary is stale.
/// The chunk fields match the Mac's <c>MeetingListService.SummaryMeta</c>. <c>failed</c> and
/// <c>attempts</c> are written only when a requested summary did not land.
/// </summary>
public sealed record MeetingSummaryMeta(
    [property: JsonPropertyName("chunkCount")] int ChunkCount,
    [property: JsonPropertyName("lastChunkStart")] double LastChunkStart,
    // Omitted when false/0 so a successful sidecar stays the two fields a summary was built from.
    [property: JsonPropertyName("failed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Failed = false,
    [property: JsonPropertyName("attempts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Attempts = 0)
{
    /// <summary>Meta for these chunks. The last start is the greatest start, or 0 when there are none.</summary>
    public static MeetingSummaryMeta FromChunks(IReadOnlyList<TranscriptChunk> chunks)
    {
        var last = 0.0;
        for (var i = 0; i < chunks.Count; i++)
            if (chunks[i].StartSeconds > last) last = chunks[i].StartSeconds;
        return new MeetingSummaryMeta(chunks.Count, last);
    }

    /// <summary>
    /// True when the summary still covers <paramref name="chunks"/>. A summary without meta predates
    /// this check and counts as matching, so old meetings are not regenerated wholesale.
    /// <see cref="MeetingSummaryMeta.Failed"/> and <see cref="MeetingSummaryMeta.Attempts"/> are not
    /// part of the comparison: staleness is "built from a shorter transcript".
    /// </summary>
    public static bool Matches(MeetingSummaryMeta? meta, IReadOnlyList<TranscriptChunk> chunks)
    {
        if (meta == null) return true;
        var now = FromChunks(chunks);
        return meta.ChunkCount == now.ChunkCount && meta.LastChunkStart == now.LastChunkStart;
    }
}

/// <summary>Whether an ended meeting's summary should be written again. Pure: no files, no model call.</summary>
public static class MeetingSummaryFreshness
{
    /// <summary>
    /// A summary that was requested and failed is tried again this many times in total, counting the
    /// stop that failed. The next start after that leaves it alone.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// True when the meeting is no longer recording, its transcript has at least one chunk, no audio is
    /// still waiting for a retry, and either the sidecar says this summary was requested and failed
    /// (fewer than <see cref="MaxAttempts"/> times) or a summary exists whose sidecar does not match
    /// the transcript. A missing summary with no sidecar was never requested — summaries were off, or
    /// the user deleted it — and is left alone. A sidecar-less summary counts as current. Pending audio
    /// waits: the summary is redone after the retry, not from a fragment.
    /// </summary>
    public static bool NeedsNewSummary(MeetingStatus status, bool hasSummary, MeetingSummaryMeta? meta,
        IReadOnlyList<TranscriptChunk> chunks, int pendingAudioChunks)
    {
        if (status == MeetingStatus.Recording) return false;
        if (pendingAudioChunks > 0) return false;
        if (chunks.Count == 0) return false;
        if (!hasSummary)
            return meta is { Failed: true, Attempts: < MaxAttempts };
        return !MeetingSummaryMeta.Matches(meta, chunks);
    }
}
